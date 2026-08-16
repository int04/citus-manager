\set ON_ERROR_STOP on
\pset pager off

-- Synthetic Citus workload for Citus Manager UI testing.
-- Run on coordinator. Refuses to overwrite an existing demo.

DO $guard$
BEGIN
    IF to_regnamespace('citus_demo') IS NOT NULL
       OR to_regnamespace('citus_demo_tenant_blue') IS NOT NULL THEN
        RAISE EXCEPTION
            'Demo schema already exists; run citus_demo_cleanup.sql explicitly before rebuilding';
    END IF;
END
$guard$;

CREATE SCHEMA citus_demo;

CREATE TYPE citus_demo.job_state AS ENUM
    ('queued', 'running', 'succeeded', 'failed', 'cancelled');
CREATE TYPE citus_demo.order_state AS ENUM
    ('draft', 'confirmed', 'paid', 'shipped', 'completed', 'cancelled');

-- Coordinator-local table: control-plane style workload.
CREATE TABLE citus_demo.admin_jobs (
    job_id bigint PRIMARY KEY,
    correlation_id uuid NOT NULL,
    job_name varchar(120) NOT NULL,
    state citus_demo.job_state NOT NULL,
    priority smallint NOT NULL CHECK (priority BETWEEN 1 AND 10),
    retry_count integer NOT NULL,
    scheduled_on date NOT NULL,
    scheduled_at time NOT NULL,
    timeout interval NOT NULL,
    requested_from inet NOT NULL,
    tags text[] NOT NULL,
    parameters jsonb NOT NULL,
    created_at timestamptz NOT NULL
);

-- Small shared dimension replicated to every worker.
CREATE TABLE citus_demo.products (
    product_id bigint PRIMARY KEY,
    sku varchar(32) NOT NULL UNIQUE,
    product_name text NOT NULL,
    category varchar(40) NOT NULL,
    unit_price numeric(12,2) NOT NULL CHECK (unit_price >= 0),
    weight_kg real NOT NULL,
    active boolean NOT NULL,
    available_during daterange NOT NULL,
    attributes jsonb NOT NULL,
    search_document tsvector NOT NULL,
    created_at timestamptz NOT NULL
);
SELECT create_reference_table('citus_demo.products');

-- Main row-sharded, colocated SaaS/e-commerce domain.
CREATE TABLE citus_demo.tenants (
    tenant_id bigint NOT NULL,
    tenant_uuid uuid NOT NULL,
    tenant_code varchar(24) NOT NULL,
    display_name text NOT NULL,
    plan_code char(2) NOT NULL,
    enabled boolean NOT NULL,
    credit_limit numeric(14,2) NOT NULL,
    settings jsonb NOT NULL,
    regions text[] NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id),
    UNIQUE (tenant_id, tenant_code)
);

CREATE TABLE citus_demo.customers (
    tenant_id bigint NOT NULL,
    customer_id bigint NOT NULL,
    customer_uuid uuid NOT NULL,
    full_name text NOT NULL,
    email text NOT NULL,
    birth_date date,
    loyalty_points integer NOT NULL,
    risk_score double precision NOT NULL,
    home_network cidr NOT NULL,
    preferences jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id),
    UNIQUE (tenant_id, email)
);

CREATE TABLE citus_demo.orders (
    tenant_id bigint NOT NULL,
    order_id bigint NOT NULL,
    customer_id bigint NOT NULL,
    state citus_demo.order_state NOT NULL,
    currency char(3) NOT NULL,
    subtotal numeric(14,2) NOT NULL,
    tax numeric(14,2) NOT NULL,
    shipping_address jsonb NOT NULL,
    requested_window tstzrange NOT NULL,
    ordered_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, order_id)
);

CREATE TABLE citus_demo.order_items (
    tenant_id bigint NOT NULL,
    order_id bigint NOT NULL,
    line_no smallint NOT NULL,
    product_id bigint NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    unit_price numeric(12,2) NOT NULL,
    discount_rate numeric(5,4) NOT NULL,
    metadata jsonb NOT NULL,
    PRIMARY KEY (tenant_id, order_id, line_no)
);

SELECT create_distributed_table(
    'citus_demo.tenants', 'tenant_id', colocate_with => 'none', shard_count => 8);
SELECT create_distributed_table(
    'citus_demo.customers', 'tenant_id', colocate_with => 'citus_demo.tenants');
SELECT create_distributed_table(
    'citus_demo.orders', 'tenant_id', colocate_with => 'citus_demo.tenants');
SELECT create_distributed_table(
    'citus_demo.order_items', 'tenant_id', colocate_with => 'citus_demo.tenants');

ALTER TABLE citus_demo.customers
    ADD CONSTRAINT fk_customers_tenant
    FOREIGN KEY (tenant_id) REFERENCES citus_demo.tenants (tenant_id);
ALTER TABLE citus_demo.orders
    ADD CONSTRAINT fk_orders_customer
    FOREIGN KEY (tenant_id, customer_id)
    REFERENCES citus_demo.customers (tenant_id, customer_id);
ALTER TABLE citus_demo.order_items
    ADD CONSTRAINT fk_order_items_order
    FOREIGN KEY (tenant_id, order_id)
    REFERENCES citus_demo.orders (tenant_id, order_id);
ALTER TABLE citus_demo.order_items
    ADD CONSTRAINT fk_order_items_product
    FOREIGN KEY (product_id) REFERENCES citus_demo.products (product_id);

CREATE INDEX ix_customers_tenant_created
    ON citus_demo.customers (tenant_id, created_at DESC);
CREATE INDEX ix_orders_tenant_ordered
    ON citus_demo.orders (tenant_id, ordered_at DESC);
CREATE INDEX ix_orders_open
    ON citus_demo.orders (tenant_id, updated_at DESC)
    WHERE state IN ('draft', 'confirmed', 'paid', 'shipped');

-- Time series: Citus hash sharding by tenant + PostgreSQL range partitioning by time.
CREATE TABLE citus_demo.sensor_events (
    tenant_id bigint NOT NULL,
    event_id bigint NOT NULL,
    device_uuid uuid NOT NULL,
    event_time timestamptz NOT NULL,
    event_type varchar(30) NOT NULL,
    temperature numeric(6,2),
    humidity real,
    location point,
    flags bit(8) NOT NULL,
    payload jsonb NOT NULL,
    raw_packet bytea NOT NULL,
    PRIMARY KEY (tenant_id, event_id, event_time)
) PARTITION BY RANGE (event_time);

SELECT create_distributed_table(
    'citus_demo.sensor_events', 'tenant_id', colocate_with => 'citus_demo.tenants');

CREATE TABLE citus_demo.sensor_events_2026_05
    PARTITION OF citus_demo.sensor_events
    FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00');
CREATE TABLE citus_demo.sensor_events_2026_06
    PARTITION OF citus_demo.sensor_events
    FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');
CREATE TABLE citus_demo.sensor_events_2026_07
    PARTITION OF citus_demo.sensor_events
    FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00');
CREATE TABLE citus_demo.sensor_events_2026_08
    PARTITION OF citus_demo.sensor_events
    FOR VALUES FROM ('2026-08-01 00:00:00+00') TO ('2026-09-01 00:00:00+00');
CREATE TABLE citus_demo.sensor_events_2026_09
    PARTITION OF citus_demo.sensor_events
    FOR VALUES FROM ('2026-09-01 00:00:00+00') TO ('2026-10-01 00:00:00+00');

CREATE INDEX ix_sensor_events_tenant_time
    ON citus_demo.sensor_events (tenant_id, event_time DESC, event_id DESC);
CREATE INDEX ix_sensor_events_time_brin
    ON citus_demo.sensor_events USING brin (event_time);

-- Independent colocation group + broad PostgreSQL type coverage.
CREATE TABLE citus_demo.audit_logs (
    workspace_id bigint NOT NULL,
    audit_id bigint NOT NULL,
    trace_id uuid NOT NULL,
    severity smallint NOT NULL,
    success boolean NOT NULL,
    event_date date NOT NULL,
    event_clock time with time zone NOT NULL,
    event_at timestamptz NOT NULL,
    duration interval NOT NULL,
    amount_delta money NOT NULL,
    cpu_ratio double precision NOT NULL,
    country_code char(2) NOT NULL,
    actor varchar(80) NOT NULL,
    action text NOT NULL,
    fingerprint bytea NOT NULL,
    details jsonb NOT NULL,
    labels text[] NOT NULL,
    counters integer[] NOT NULL,
    source_ip inet NOT NULL,
    source_network cidr NOT NULL,
    source_mac macaddr NOT NULL,
    permission_mask bit(8) NOT NULL,
    affected_ids int8range NOT NULL,
    search_document tsvector NOT NULL,
    PRIMARY KEY (workspace_id, audit_id)
);

SELECT create_distributed_table(
    'citus_demo.audit_logs', 'workspace_id', colocate_with => 'none', shard_count => 8);
CREATE INDEX ix_audit_logs_workspace_time
    ON citus_demo.audit_logs (workspace_id, event_at DESC);
CREATE INDEX ix_audit_logs_details
    ON citus_demo.audit_logs USING gin (details);

-- Schema-based sharding: tables need no row-level distribution column.
CREATE SCHEMA citus_demo_tenant_blue;
SELECT citus_schema_distribute('citus_demo_tenant_blue');

CREATE TABLE citus_demo_tenant_blue.invoices (
    invoice_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    invoice_no varchar(30) NOT NULL UNIQUE,
    customer_name text NOT NULL,
    issued_on date NOT NULL,
    due_on date NOT NULL,
    subtotal numeric(14,2) NOT NULL,
    tax numeric(14,2) NOT NULL,
    paid boolean NOT NULL,
    line_summary jsonb NOT NULL,
    notes text,
    created_at timestamp without time zone NOT NULL
);

-- Seed: deterministic enough for repeatable UI tests; varied enough for filtering/sorting.
INSERT INTO citus_demo.admin_jobs
SELECT i,
       ('10000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'cluster-operation-' || i,
       (ARRAY['queued','running','succeeded','failed','cancelled']::citus_demo.job_state[])[1 + (i % 5)],
       1 + (i % 10), i % 4,
       DATE '2026-01-01' + (i % 240),
       TIME '08:00:00' + (i % 43200) * INTERVAL '1 second',
       INTERVAL '30 seconds' + (i % 90) * INTERVAL '1 second',
       ('10.' || (i % 255) || '.' || ((i / 255) % 255) || '.' || (1 + i % 253))::inet,
       ARRAY['citus', 'demo', CASE WHEN i % 2 = 0 THEN 'topology' ELSE 'query' END],
       jsonb_build_object('worker', 1 + i % 2, 'dryRun', i % 3 = 0, 'attempt', i % 4),
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + i * INTERVAL '1 minute'
FROM generate_series(1, 5000) AS g(i);

INSERT INTO citus_demo.products
SELECT i,
       'SKU-' || lpad(i::text, 7, '0'),
       'Synthetic product ' || i,
       (ARRAY['hardware','software','service','subscription','accessory'])[1 + (i % 5)],
       round((5 + (i % 50000) / 37.0)::numeric, 2),
       ((i % 2000) / 100.0)::real,
       i % 13 <> 0,
       daterange(DATE '2025-01-01' + (i % 365), DATE '2028-01-01' + (i % 365), '[)'),
       jsonb_build_object('color', (ARRAY['red','blue','green','black'])[1 + i % 4],
                          'warrantyMonths', 6 + i % 31,
                          'fragile', i % 7 = 0),
       to_tsvector('simple', 'Synthetic product ' || i || ' SKU ' || i),
       TIMESTAMPTZ '2025-01-01 00:00:00+00' + i * INTERVAL '10 minutes'
FROM generate_series(1, 5000) AS g(i);

INSERT INTO citus_demo.tenants
SELECT i,
       ('20000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'TEN-' || lpad(i::text, 6, '0'),
       'Tenant ' || i,
       (ARRAY['FR','ST','PR'])[1 + i % 3],
       i % 17 <> 0,
       round((1000 + i * 3.17)::numeric, 2),
       jsonb_build_object('timezone', (ARRAY['Asia/Ho_Chi_Minh','UTC','Asia/Singapore'])[1 + i % 3],
                          'mfaRequired', i % 2 = 0),
       ARRAY[(ARRAY['ap-southeast','eu-west','us-east'])[1 + i % 3]],
       TIMESTAMPTZ '2024-01-01 00:00:00+00' + i * INTERVAL '2 hours'
FROM generate_series(1, 5000) AS g(i);

INSERT INTO citus_demo.customers
SELECT 1 + ((i - 1) % 5000), i,
       ('30000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'Customer ' || i,
       'customer' || i || '@example.test',
       DATE '1960-01-01' + (i % 18000),
       i % 25000,
       (i % 1000) / 1000.0,
       ('10.' || (i % 255) || '.0.0/16')::cidr,
       jsonb_build_object('language', (ARRAY['vi','en','ja'])[1 + i % 3],
                          'marketing', i % 4 <> 0),
       TIMESTAMPTZ '2025-01-01 00:00:00+00' + i * INTERVAL '30 minutes'
FROM generate_series(1, 8000) AS g(i);

INSERT INTO citus_demo.orders
SELECT 1 + ((((i - 1) % 8000)) % 5000),
       i,
       1 + ((i - 1) % 8000),
       (ARRAY['draft','confirmed','paid','shipped','completed','cancelled']::citus_demo.order_state[])[1 + i % 6],
       (ARRAY['VND','USD','EUR'])[1 + i % 3],
       round((20 + (i % 10000) / 13.0)::numeric, 2),
       round((2 + (i % 1000) / 97.0)::numeric, 2),
       jsonb_build_object('city', (ARRAY['HCM','Ha Noi','Da Nang'])[1 + i % 3],
                          'postalCode', lpad((700000 + i % 99999)::text, 6, '0')),
       tstzrange(TIMESTAMPTZ '2026-01-01 00:00:00+00' + i * INTERVAL '20 minutes',
                 TIMESTAMPTZ '2026-01-01 02:00:00+00' + i * INTERVAL '20 minutes', '[)'),
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + i * INTERVAL '20 minutes',
       TIMESTAMPTZ '2026-01-01 00:05:00+00' + i * INTERVAL '20 minutes'
FROM generate_series(1, 9000) AS g(i);

INSERT INTO citus_demo.order_items
SELECT 1 + (((((i - 1) % 9000)) % 8000) % 5000),
       1 + ((i - 1) % 9000),
       1 + ((i - 1) / 9000),
       1 + ((i - 1) % 5000),
       1 + i % 5,
       round((5 + (i % 5000) / 31.0)::numeric, 2),
       round(((i % 25) / 100.0)::numeric, 4),
       jsonb_build_object('giftWrap', i % 9 = 0, 'warehouse', 1 + i % 4)
FROM generate_series(1, 10000) AS g(i);

INSERT INTO citus_demo.sensor_events
SELECT 1 + ((i - 1) % 5000), i,
       ('40000000-0000-0000-0000-' || lpad((1 + i % 3000)::text, 12, '0'))::uuid,
       TIMESTAMPTZ '2026-05-01 00:00:00+00' + (i % 153) * INTERVAL '1 day' + (i % 86400) * INTERVAL '1 second',
       (ARRAY['temperature','humidity','motion','battery'])[1 + i % 4],
       round((15 + (i % 2500) / 100.0)::numeric, 2),
       ((30 + i % 7000) / 100.0)::real,
       point(106.0 + (i % 1000) / 10000.0, 10.0 + (i % 1000) / 10000.0),
       (i % 256)::bit(8),
       jsonb_build_object('firmware', 'v' || (1 + i % 5), 'signalDbm', -30 - i % 70),
       decode(md5('packet-' || i), 'hex')
FROM generate_series(1, 10000) AS g(i);

INSERT INTO citus_demo.audit_logs
SELECT 1 + ((i - 1) % 2500), i,
       ('50000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       i % 8, i % 11 <> 0,
       DATE '2026-01-01' + i % 240,
       (TIME WITH TIME ZONE '09:00:00+07') + (i % 3600) * INTERVAL '1 second',
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + i * INTERVAL '15 minutes',
       (i % 5000) * INTERVAL '1 millisecond',
       ((i % 20000) - 10000)::numeric::money,
       (i % 10000) / 10000.0,
       (ARRAY['VN','SG','JP','US'])[1 + i % 4],
       'actor-' || i,
       (ARRAY['login','query','rebalance','drain','export'])[1 + i % 5],
       decode(md5('audit-' || i), 'hex'),
       jsonb_build_object('nodeId', 1 + i % 3, 'rows', i % 1000, 'approved', i % 2 = 0),
       ARRAY['demo', CASE WHEN i % 2 = 0 THEN 'read' ELSE 'write' END],
       ARRAY[i % 10, i % 100, i % 1000],
       ('172.16.' || (i % 255) || '.' || (1 + i % 253))::inet,
       ('172.16.' || (i % 255) || '.0/24')::cidr,
       ('02:00:' || substr(md5(i::text), 1, 2) || ':' || substr(md5(i::text), 3, 2) || ':' ||
        substr(md5(i::text), 5, 2) || ':' || substr(md5(i::text), 7, 2))::macaddr,
       (i % 256)::bit(8),
       int8range(i, i + 1 + i % 100, '[)'),
       to_tsvector('simple', 'actor ' || i || ' action ' || (ARRAY['login','query','rebalance','drain','export'])[1 + i % 5])
FROM generate_series(1, 8000) AS g(i);

INSERT INTO citus_demo_tenant_blue.invoices
    (invoice_id, invoice_no, customer_name, issued_on, due_on, subtotal, tax, paid,
     line_summary, notes, created_at)
SELECT i,
       'INV-' || lpad(i::text, 8, '0'),
       'Blue customer ' || i,
       DATE '2025-01-01' + i % 600,
       DATE '2025-01-15' + i % 600,
       round((100 + (i % 50000) / 17.0)::numeric, 2),
       round((10 + (i % 5000) / 53.0)::numeric, 2),
       i % 5 <> 0,
       jsonb_build_array(
           jsonb_build_object('sku', 'SKU-' || lpad((1 + i % 5000)::text, 7, '0'),
                              'qty', 1 + i % 5)),
       CASE WHEN i % 10 = 0 THEN 'Manual review' END,
       TIMESTAMP '2025-01-01 00:00:00' + i * INTERVAL '1 hour'
FROM generate_series(1, 6000) AS g(i);

ANALYZE citus_demo.admin_jobs;
ANALYZE citus_demo.products;
ANALYZE citus_demo.tenants;
ANALYZE citus_demo.customers;
ANALYZE citus_demo.orders;
ANALYZE citus_demo.order_items;
ANALYZE citus_demo.sensor_events;
ANALYZE citus_demo.audit_logs;
ANALYZE citus_demo_tenant_blue.invoices;

\echo 'Citus demo seed complete.'
