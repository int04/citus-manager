\set ON_ERROR_STOP on
\pset pager off
\timing on

CREATE INDEX IF NOT EXISTS ix_merchants_zone_category
    ON citus_scale.merchants (home_zone_id, category_code, active);
CREATE INDEX IF NOT EXISTS ix_merchants_profile_gin
    ON citus_scale.merchants USING gin (profile);

CREATE INDEX IF NOT EXISTS ix_organizations_zone_created
    ON citus_scale.organizations (home_zone_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_organizations_settings_gin
    ON citus_scale.organizations USING gin (settings);
CREATE INDEX IF NOT EXISTS ix_app_users_org_seen
    ON citus_scale.app_users (org_id, last_seen_at DESC, user_id DESC);
CREATE INDEX IF NOT EXISTS ix_app_users_profile_gin
    ON citus_scale.app_users USING gin (profile);
CREATE INDEX IF NOT EXISTS ix_subscriptions_org_state
    ON citus_scale.subscriptions (org_id, state, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_subscriptions_active
    ON citus_scale.subscriptions (org_id, billing_period)
    WHERE state IN ('trial', 'active', 'past_due');
CREATE INDEX IF NOT EXISTS ix_usage_events_org_time
    ON citus_scale.usage_events (org_id, event_time DESC, event_id DESC);
CREATE INDEX IF NOT EXISTS ix_usage_events_time_brin
    ON citus_scale.usage_events USING brin (event_time);
CREATE INDEX IF NOT EXISTS ix_usage_rollups_org_bucket
    ON citus_scale.usage_daily_rollups (org_id, bucket_date DESC, metric_name);

CREATE INDEX IF NOT EXISTS ix_bank_accounts_status_currency
    ON citus_scale.bank_accounts (status, currency, opened_on DESC);
CREATE INDEX IF NOT EXISTS ix_ledger_account_booked
    ON citus_scale.ledger_entries (account_id, booked_at DESC, entry_id DESC);
CREATE INDEX IF NOT EXISTS ix_ledger_value_date_brin
    ON citus_scale.ledger_entries USING brin (value_date);
CREATE INDEX IF NOT EXISTS ix_payments_account_authorized
    ON citus_scale.payment_transactions (account_id, authorized_at DESC, transaction_id DESC);
CREATE INDEX IF NOT EXISTS ix_payments_merchant_authorized
    ON citus_scale.payment_transactions (merchant_id, authorized_at DESC);
CREATE INDEX IF NOT EXISTS ix_payments_review
    ON citus_scale.payment_transactions (account_id, fraud_score DESC)
    WHERE status IN ('declined', 'reversed');

CREATE INDEX IF NOT EXISTS ix_conversations_created
    ON citus_scale.conversations (created_at DESC);
CREATE INDEX IF NOT EXISTS ix_messages_conversation_sent
    ON citus_scale.messages (conversation_id, sent_at DESC, message_id DESC);
CREATE INDEX IF NOT EXISTS ix_messages_search_gin
    ON citus_scale.messages USING gin (body_search);
CREATE INDEX IF NOT EXISTS ix_reactions_message
    ON citus_scale.message_reactions (conversation_id, message_id, reacted_at DESC);

CREATE INDEX IF NOT EXISTS ix_shipments_state_created
    ON citus_scale.shipments (state, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_tracking_shipment_time
    ON citus_scale.tracking_events (shipment_id, event_time DESC, tracking_event_id DESC);
CREATE INDEX IF NOT EXISTS ix_tracking_time_brin
    ON citus_scale.tracking_events USING brin (event_time);

CREATE INDEX IF NOT EXISTS ix_patients_org_birth
    ON citus_scale.patients (care_org_id, birth_date, patient_id);
CREATE INDEX IF NOT EXISTS ix_patients_contact_gin
    ON citus_scale.patients USING gin (contact);
CREATE INDEX IF NOT EXISTS ix_encounters_patient_started
    ON citus_scale.encounters (care_org_id, patient_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_observations_encounter_time
    ON citus_scale.clinical_observations (care_org_id, encounter_id, observed_at DESC);
CREATE INDEX IF NOT EXISTS ix_observations_abnormal
    ON citus_scale.clinical_observations (care_org_id, observed_at DESC)
    WHERE abnormal;

CREATE INDEX IF NOT EXISTS ix_tickets_org_state_updated
    ON citus_scale.support_tickets (org_id, state, updated_at DESC, ticket_id DESC);
CREATE INDEX IF NOT EXISTS ix_tickets_open_priority
    ON citus_scale.support_tickets (org_id, priority DESC, updated_at)
    WHERE state NOT IN ('resolved', 'closed');
CREATE INDEX IF NOT EXISTS ix_ticket_comments_ticket_time
    ON citus_scale.ticket_comments (org_id, ticket_id, created_at, comment_id);
CREATE INDEX IF NOT EXISTS ix_ticket_comments_search_gin
    ON citus_scale.ticket_comments USING gin (body_search);

ANALYZE citus_scale.feature_flags;
ANALYZE citus_scale.geo_zones;
ANALYZE citus_scale.merchants;
ANALYZE citus_scale.organizations;
ANALYZE citus_scale.app_users;
ANALYZE citus_scale.subscriptions;
ANALYZE citus_scale.usage_events;
ANALYZE citus_scale.usage_daily_rollups;
ANALYZE citus_scale.bank_accounts;
ANALYZE citus_scale.ledger_entries;
ANALYZE citus_scale.payment_transactions;
ANALYZE citus_scale.conversations;
ANALYZE citus_scale.messages;
ANALYZE citus_scale.message_reactions;
ANALYZE citus_scale.shipments;
ANALYZE citus_scale.tracking_events;
ANALYZE citus_scale.patients;
ANALYZE citus_scale.encounters;
ANALYZE citus_scale.clinical_observations;
ANALYZE citus_scale.support_tickets;
ANALYZE citus_scale.ticket_comments;

\echo 'citus_scale indexes and statistics complete.'
