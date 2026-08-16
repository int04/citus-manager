\set ON_ERROR_STOP on
\pset pager off

SELECT table_name,
       citus_table_type,
       distribution_column,
       colocation_id,
       shard_count,
       table_size,
       access_method
FROM citus_tables
WHERE table_name::text LIKE 'citus_demo.%'
   OR table_name::text LIKE 'citus_demo_tenant_blue.%'
ORDER BY table_name;

SELECT * FROM (VALUES
    ('citus_demo.admin_jobs', (SELECT count(*) FROM citus_demo.admin_jobs), 5000),
    ('citus_demo.products', (SELECT count(*) FROM citus_demo.products), 5000),
    ('citus_demo.tenants', (SELECT count(*) FROM citus_demo.tenants), 5000),
    ('citus_demo.customers', (SELECT count(*) FROM citus_demo.customers), 8000),
    ('citus_demo.orders', (SELECT count(*) FROM citus_demo.orders), 9000),
    ('citus_demo.order_items', (SELECT count(*) FROM citus_demo.order_items), 10000),
    ('citus_demo.sensor_events', (SELECT count(*) FROM citus_demo.sensor_events), 10000),
    ('citus_demo.audit_logs', (SELECT count(*) FROM citus_demo.audit_logs), 8000),
    ('citus_demo_tenant_blue.invoices', (SELECT count(*) FROM citus_demo_tenant_blue.invoices), 6000)
) AS counts(table_name, actual_rows, expected_rows)
ORDER BY table_name;

SELECT table_name,
       count(DISTINCT shardid) AS shard_count,
       count(DISTINCT nodename || ':' || nodeport) AS workers_with_placements,
       pg_size_pretty(sum(shard_size)) AS total_shard_size
FROM citus_shards
WHERE table_name::text LIKE 'citus_demo.%'
   OR table_name::text LIKE 'citus_demo_tenant_blue.%'
GROUP BY table_name
ORDER BY table_name;

SELECT parent.relname AS partitioned_table,
       child.relname AS partition_name,
       pg_get_expr(child.relpartbound, child.oid) AS partition_bound
FROM pg_inherits i
JOIN pg_class parent ON parent.oid = i.inhparent
JOIN pg_class child ON child.oid = i.inhrelid
JOIN pg_namespace n ON n.oid = parent.relnamespace
WHERE n.nspname = 'citus_demo'
  AND parent.relname = 'sensor_events'
ORDER BY child.relname;

EXPLAIN (COSTS OFF)
SELECT *
FROM citus_demo.orders
WHERE tenant_id = 42
ORDER BY ordered_at DESC
LIMIT 10;

EXPLAIN (COSTS OFF)
SELECT event_type, count(*)
FROM citus_demo.sensor_events
WHERE tenant_id = 42
  AND event_time >= TIMESTAMPTZ '2026-08-01 00:00:00+00'
  AND event_time <  TIMESTAMPTZ '2026-09-01 00:00:00+00'
GROUP BY event_type;
