\set ON_ERROR_STOP on
\pset pager off
\timing on

-- Large synthetic expansion pack. Run on coordinator.
DO $guard$
BEGIN
    IF to_regnamespace('citus_scale') IS NOT NULL THEN
        RAISE EXCEPTION 'Schema citus_scale already exists; refusing to overwrite it';
    END IF;
END
$guard$;

CREATE SCHEMA citus_scale;

CREATE TYPE citus_scale.subscription_state AS ENUM
    ('trial', 'active', 'past_due', 'paused', 'cancelled');
CREATE TYPE citus_scale.ledger_direction AS ENUM ('debit', 'credit');
CREATE TYPE citus_scale.shipment_state AS ENUM
    ('created', 'picked_up', 'in_transit', 'customs', 'delivered', 'returned', 'lost');
CREATE TYPE citus_scale.ticket_state AS ENUM
    ('new', 'open', 'waiting_customer', 'waiting_internal', 'resolved', 'closed');

-- Local control/config workload.
CREATE TABLE citus_scale.feature_flags (
    flag_id integer PRIMARY KEY,
    flag_key varchar(100) NOT NULL UNIQUE,
    enabled boolean NOT NULL,
    rollout_percent numeric(5,2) NOT NULL,
    audience jsonb NOT NULL,
    valid_during tstzrange NOT NULL,
    owners text[] NOT NULL,
    updated_at timestamptz NOT NULL
);

-- Shared dimensions replicated to coordinator + workers.
CREATE TABLE citus_scale.geo_zones (
    zone_id integer PRIMARY KEY,
    zone_code varchar(20) NOT NULL UNIQUE,
    country_code char(2) NOT NULL,
    currency_code char(3) NOT NULL,
    timezone_name text NOT NULL,
    tax_rate numeric(7,4) NOT NULL,
    service_area polygon,
    metadata jsonb NOT NULL
);
SELECT create_reference_table('citus_scale.geo_zones');

CREATE TABLE citus_scale.merchants (
    merchant_id bigint PRIMARY KEY,
    merchant_uuid uuid NOT NULL,
    merchant_code varchar(32) NOT NULL UNIQUE,
    merchant_name text NOT NULL,
    category_code char(4) NOT NULL,
    home_zone_id integer NOT NULL REFERENCES citus_scale.geo_zones(zone_id),
    risk_band smallint NOT NULL,
    active boolean NOT NULL,
    accepted_currencies text[] NOT NULL,
    profile jsonb NOT NULL,
    created_at timestamptz NOT NULL
);
SELECT create_reference_table('citus_scale.merchants');

-- Platform/SaaS domain. One org = routing + transaction boundary.
CREATE TABLE citus_scale.organizations (
    org_id bigint NOT NULL,
    org_uuid uuid NOT NULL,
    org_slug varchar(80) NOT NULL,
    legal_name text NOT NULL,
    home_zone_id integer NOT NULL,
    employee_band int4range NOT NULL,
    data_residency text[] NOT NULL,
    settings jsonb NOT NULL,
    active boolean NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (org_id),
    UNIQUE (org_id, org_slug)
);

CREATE TABLE citus_scale.app_users (
    org_id bigint NOT NULL,
    user_id bigint NOT NULL,
    user_uuid uuid NOT NULL,
    email text NOT NULL,
    display_name text NOT NULL,
    role_codes text[] NOT NULL,
    last_ip inet NOT NULL,
    profile jsonb NOT NULL,
    last_seen_at timestamptz,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, user_id),
    UNIQUE (org_id, email)
);

CREATE TABLE citus_scale.subscriptions (
    org_id bigint NOT NULL,
    subscription_id bigint NOT NULL,
    state citus_scale.subscription_state NOT NULL,
    plan_code varchar(30) NOT NULL,
    seats integer NOT NULL,
    unit_price numeric(12,2) NOT NULL,
    billing_period daterange NOT NULL,
    payment_terms interval NOT NULL,
    metadata jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, subscription_id)
);

CREATE TABLE citus_scale.usage_events (
    org_id bigint NOT NULL,
    event_id bigint NOT NULL,
    user_id bigint,
    event_time timestamptz NOT NULL,
    metric_name varchar(60) NOT NULL,
    metric_value numeric(18,4) NOT NULL,
    dimensions jsonb NOT NULL,
    source_ip inet NOT NULL,
    trace_id uuid NOT NULL,
    PRIMARY KEY (org_id, event_id, event_time)
) PARTITION BY RANGE (event_time);

CREATE TABLE citus_scale.usage_daily_rollups (
    org_id bigint NOT NULL,
    rollup_id bigint NOT NULL,
    bucket_date date NOT NULL,
    metric_name varchar(60) NOT NULL,
    event_count bigint NOT NULL,
    metric_sum numeric(24,4) NOT NULL,
    p95_value double precision NOT NULL,
    dimensions jsonb NOT NULL,
    PRIMARY KEY (org_id, rollup_id),
    UNIQUE (org_id, bucket_date, metric_name, rollup_id)
);

SELECT create_distributed_table(
    'citus_scale.organizations', 'org_id', colocate_with => 'none', shard_count => 16);
SELECT create_distributed_table(
    'citus_scale.app_users', 'org_id', colocate_with => 'citus_scale.organizations');
SELECT create_distributed_table(
    'citus_scale.subscriptions', 'org_id', colocate_with => 'citus_scale.organizations');
SELECT create_distributed_table(
    'citus_scale.usage_events', 'org_id', colocate_with => 'citus_scale.organizations');
SELECT create_distributed_table(
    'citus_scale.usage_daily_rollups', 'org_id', colocate_with => 'citus_scale.organizations');

ALTER TABLE citus_scale.organizations
    ADD CONSTRAINT fk_organizations_zone FOREIGN KEY (home_zone_id)
    REFERENCES citus_scale.geo_zones(zone_id);
ALTER TABLE citus_scale.app_users
    ADD CONSTRAINT fk_app_users_org FOREIGN KEY (org_id)
    REFERENCES citus_scale.organizations(org_id);
ALTER TABLE citus_scale.subscriptions
    ADD CONSTRAINT fk_subscriptions_org FOREIGN KEY (org_id)
    REFERENCES citus_scale.organizations(org_id);
ALTER TABLE citus_scale.usage_daily_rollups
    ADD CONSTRAINT fk_usage_rollups_org FOREIGN KEY (org_id)
    REFERENCES citus_scale.organizations(org_id);

CREATE TABLE citus_scale.usage_events_2025_09 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2025-09-01 00:00:00+00') TO ('2025-10-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2025_10 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2025-10-01 00:00:00+00') TO ('2025-11-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2025_11 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2025-11-01 00:00:00+00') TO ('2025-12-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2025_12 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2025-12-01 00:00:00+00') TO ('2026-01-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_01 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-02-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_02 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-02-01 00:00:00+00') TO ('2026-03-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_03 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_04 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-04-01 00:00:00+00') TO ('2026-05-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_05 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_06 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_07 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00');
CREATE TABLE citus_scale.usage_events_2026_08 PARTITION OF citus_scale.usage_events
    FOR VALUES FROM ('2026-08-01 00:00:00+00') TO ('2026-09-01 00:00:00+00');

-- Finance domain. account_id keeps balance + ledger + payment local.
CREATE TABLE citus_scale.bank_accounts (
    account_id bigint NOT NULL,
    account_uuid uuid NOT NULL,
    owner_ref varchar(80) NOT NULL,
    account_type varchar(20) NOT NULL,
    currency char(3) NOT NULL,
    balance numeric(20,4) NOT NULL,
    credit_limit numeric(20,4) NOT NULL,
    status varchar(20) NOT NULL,
    opened_on date NOT NULL,
    risk_profile jsonb NOT NULL,
    PRIMARY KEY (account_id)
);

CREATE TABLE citus_scale.ledger_entries (
    account_id bigint NOT NULL,
    entry_id bigint NOT NULL,
    direction citus_scale.ledger_direction NOT NULL,
    amount numeric(20,4) NOT NULL,
    currency char(3) NOT NULL,
    balance_after numeric(20,4) NOT NULL,
    booked_at timestamptz NOT NULL,
    value_date date NOT NULL,
    reference varchar(80) NOT NULL,
    tags text[] NOT NULL,
    metadata jsonb NOT NULL,
    PRIMARY KEY (account_id, entry_id)
);

CREATE TABLE citus_scale.payment_transactions (
    account_id bigint NOT NULL,
    transaction_id bigint NOT NULL,
    merchant_id bigint NOT NULL,
    amount numeric(20,4) NOT NULL,
    currency char(3) NOT NULL,
    status varchar(20) NOT NULL,
    authorized_at timestamptz NOT NULL,
    settled_at timestamptz,
    card_fingerprint bytea NOT NULL,
    location point,
    fraud_score double precision NOT NULL,
    attributes jsonb NOT NULL,
    PRIMARY KEY (account_id, transaction_id)
);

SELECT create_distributed_table(
    'citus_scale.bank_accounts', 'account_id', colocate_with => 'none', shard_count => 16);
SELECT create_distributed_table(
    'citus_scale.ledger_entries', 'account_id', colocate_with => 'citus_scale.bank_accounts');
SELECT create_distributed_table(
    'citus_scale.payment_transactions', 'account_id', colocate_with => 'citus_scale.bank_accounts');
ALTER TABLE citus_scale.ledger_entries
    ADD CONSTRAINT fk_ledger_account FOREIGN KEY (account_id)
    REFERENCES citus_scale.bank_accounts(account_id);
ALTER TABLE citus_scale.payment_transactions
    ADD CONSTRAINT fk_payment_account FOREIGN KEY (account_id)
    REFERENCES citus_scale.bank_accounts(account_id);
ALTER TABLE citus_scale.payment_transactions
    ADD CONSTRAINT fk_payment_merchant FOREIGN KEY (merchant_id)
    REFERENCES citus_scale.merchants(merchant_id);

-- Social/chat domain. conversation_id = distribution boundary.
CREATE TABLE citus_scale.conversations (
    conversation_id bigint NOT NULL,
    conversation_uuid uuid NOT NULL,
    conversation_type varchar(20) NOT NULL,
    title text,
    participant_ids bigint[] NOT NULL,
    metadata jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (conversation_id)
);

CREATE TABLE citus_scale.messages (
    conversation_id bigint NOT NULL,
    message_id bigint NOT NULL,
    sender_id bigint NOT NULL,
    body text NOT NULL,
    body_search tsvector NOT NULL,
    attachment_refs text[] NOT NULL,
    moderation jsonb NOT NULL,
    sent_at timestamptz NOT NULL,
    edited_at timestamptz,
    PRIMARY KEY (conversation_id, message_id)
);

CREATE TABLE citus_scale.message_reactions (
    conversation_id bigint NOT NULL,
    reaction_id bigint NOT NULL,
    message_id bigint NOT NULL,
    user_id bigint NOT NULL,
    reaction_code varchar(20) NOT NULL,
    reacted_at timestamptz NOT NULL,
    PRIMARY KEY (conversation_id, reaction_id)
);

SELECT create_distributed_table(
    'citus_scale.conversations', 'conversation_id', colocate_with => 'none', shard_count => 16);
SELECT create_distributed_table(
    'citus_scale.messages', 'conversation_id', colocate_with => 'citus_scale.conversations');
SELECT create_distributed_table(
    'citus_scale.message_reactions', 'conversation_id', colocate_with => 'citus_scale.conversations');
ALTER TABLE citus_scale.messages
    ADD CONSTRAINT fk_messages_conversation FOREIGN KEY (conversation_id)
    REFERENCES citus_scale.conversations(conversation_id);
ALTER TABLE citus_scale.message_reactions
    ADD CONSTRAINT fk_reactions_message FOREIGN KEY (conversation_id, message_id)
    REFERENCES citus_scale.messages(conversation_id, message_id);

-- Logistics domain + time partitions.
CREATE TABLE citus_scale.shipments (
    shipment_id bigint NOT NULL,
    tracking_number varchar(40) NOT NULL,
    state citus_scale.shipment_state NOT NULL,
    origin_zone_id integer NOT NULL,
    destination_zone_id integer NOT NULL,
    service_level varchar(20) NOT NULL,
    weight_kg numeric(10,3) NOT NULL,
    dimensions_cm integer[] NOT NULL,
    insured_value numeric(16,2) NOT NULL,
    labels jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (shipment_id),
    UNIQUE (shipment_id, tracking_number)
);

CREATE TABLE citus_scale.tracking_events (
    shipment_id bigint NOT NULL,
    tracking_event_id bigint NOT NULL,
    event_time timestamptz NOT NULL,
    event_code varchar(30) NOT NULL,
    facility_code varchar(30) NOT NULL,
    coordinates point,
    scanner_ip inet NOT NULL,
    raw_payload jsonb NOT NULL,
    PRIMARY KEY (shipment_id, tracking_event_id, event_time)
) PARTITION BY RANGE (event_time);

SELECT create_distributed_table(
    'citus_scale.shipments', 'shipment_id', colocate_with => 'none', shard_count => 16);
SELECT create_distributed_table(
    'citus_scale.tracking_events', 'shipment_id', colocate_with => 'citus_scale.shipments');
ALTER TABLE citus_scale.shipments
    ADD CONSTRAINT fk_shipments_origin FOREIGN KEY (origin_zone_id)
    REFERENCES citus_scale.geo_zones(zone_id);
ALTER TABLE citus_scale.shipments
    ADD CONSTRAINT fk_shipments_destination FOREIGN KEY (destination_zone_id)
    REFERENCES citus_scale.geo_zones(zone_id);

CREATE TABLE citus_scale.tracking_events_2026_01 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-02-01 00:00:00+00');
CREATE TABLE citus_scale.tracking_events_2026_02 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-02-01 00:00:00+00') TO ('2026-03-01 00:00:00+00');
CREATE TABLE citus_scale.tracking_events_2026_03 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');
CREATE TABLE citus_scale.tracking_events_2026_04 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-04-01 00:00:00+00') TO ('2026-05-01 00:00:00+00');
CREATE TABLE citus_scale.tracking_events_2026_05 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00');
CREATE TABLE citus_scale.tracking_events_2026_06 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');
CREATE TABLE citus_scale.tracking_events_2026_07 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00');
CREATE TABLE citus_scale.tracking_events_2026_08 PARTITION OF citus_scale.tracking_events
    FOR VALUES FROM ('2026-08-01 00:00:00+00') TO ('2026-09-01 00:00:00+00');

-- Healthcare domain. care_org_id keeps patient/encounter/observation local.
CREATE TABLE citus_scale.patients (
    care_org_id bigint NOT NULL,
    patient_id bigint NOT NULL,
    patient_uuid uuid NOT NULL,
    full_name text NOT NULL,
    birth_date date NOT NULL,
    sex_code char(1) NOT NULL,
    blood_type varchar(3),
    allergies text[] NOT NULL,
    contact jsonb NOT NULL,
    consent_period daterange NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (care_org_id, patient_id)
);

CREATE TABLE citus_scale.encounters (
    care_org_id bigint NOT NULL,
    encounter_id bigint NOT NULL,
    patient_id bigint NOT NULL,
    encounter_type varchar(30) NOT NULL,
    status varchar(20) NOT NULL,
    started_at timestamptz NOT NULL,
    ended_at timestamptz,
    diagnosis_codes text[] NOT NULL,
    clinical_note xml,
    metadata jsonb NOT NULL,
    PRIMARY KEY (care_org_id, encounter_id)
);

CREATE TABLE citus_scale.clinical_observations (
    care_org_id bigint NOT NULL,
    observation_id bigint NOT NULL,
    encounter_id bigint NOT NULL,
    observation_code varchar(30) NOT NULL,
    observed_at timestamptz NOT NULL,
    numeric_value numeric(16,4),
    text_value text,
    unit varchar(20),
    abnormal boolean NOT NULL,
    reference_range numrange,
    details jsonb NOT NULL,
    PRIMARY KEY (care_org_id, observation_id)
);

SELECT create_distributed_table(
    'citus_scale.patients', 'care_org_id', colocate_with => 'none', shard_count => 16);
SELECT create_distributed_table(
    'citus_scale.encounters', 'care_org_id', colocate_with => 'citus_scale.patients');
SELECT create_distributed_table(
    'citus_scale.clinical_observations', 'care_org_id', colocate_with => 'citus_scale.patients');
ALTER TABLE citus_scale.encounters
    ADD CONSTRAINT fk_encounters_patient FOREIGN KEY (care_org_id, patient_id)
    REFERENCES citus_scale.patients(care_org_id, patient_id);
ALTER TABLE citus_scale.clinical_observations
    ADD CONSTRAINT fk_observations_encounter FOREIGN KEY (care_org_id, encounter_id)
    REFERENCES citus_scale.encounters(care_org_id, encounter_id);

-- Customer support domain, colocated with organizations.
CREATE TABLE citus_scale.support_tickets (
    org_id bigint NOT NULL,
    ticket_id bigint NOT NULL,
    requester_user_id bigint NOT NULL,
    state citus_scale.ticket_state NOT NULL,
    priority smallint NOT NULL,
    subject text NOT NULL,
    description text NOT NULL,
    labels text[] NOT NULL,
    sla_window tstzrange NOT NULL,
    custom_fields jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, ticket_id)
);

CREATE TABLE citus_scale.ticket_comments (
    org_id bigint NOT NULL,
    comment_id bigint NOT NULL,
    ticket_id bigint NOT NULL,
    author_user_id bigint NOT NULL,
    body text NOT NULL,
    body_search tsvector NOT NULL,
    internal_only boolean NOT NULL,
    attachments jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, comment_id)
);

SELECT create_distributed_table(
    'citus_scale.support_tickets', 'org_id', colocate_with => 'citus_scale.organizations');
SELECT create_distributed_table(
    'citus_scale.ticket_comments', 'org_id', colocate_with => 'citus_scale.organizations');
ALTER TABLE citus_scale.support_tickets
    ADD CONSTRAINT fk_tickets_org FOREIGN KEY (org_id)
    REFERENCES citus_scale.organizations(org_id);
ALTER TABLE citus_scale.ticket_comments
    ADD CONSTRAINT fk_comments_ticket FOREIGN KEY (org_id, ticket_id)
    REFERENCES citus_scale.support_tickets(org_id, ticket_id);

\echo 'citus_scale schema created.'
