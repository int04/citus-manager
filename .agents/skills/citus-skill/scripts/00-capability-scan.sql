\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== PostgreSQL and current session ==='
SELECT version();
SELECT current_database() AS database_name,
       current_user AS role_name,
       current_setting('server_version_num') AS server_version_num;

\echo '=== Installed extensions ==='
SELECT extname, extversion, extnamespace::regnamespace AS extension_schema
FROM pg_extension
ORDER BY extname;

\echo '=== Citus version when available ==='
SELECT CASE
         WHEN to_regprocedure('citus_version()') IS NULL
           THEN 'citus_version() is not available in this database'
         ELSE 'citus_version() is available; run SELECT citus_version() for the value'
       END AS capability;

SELECT 'SELECT citus_version() AS citus_version;'
WHERE to_regprocedure('citus_version()') IS NOT NULL
\gexec

\echo '=== Selected Citus function signatures ==='
SELECT n.nspname AS schema_name,
       p.proname AS function_name,
       pg_get_function_identity_arguments(p.oid) AS arguments,
       pg_get_function_result(p.oid) AS result_type,
       p.prokind AS kind
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname = ANY (ARRAY[
  'create_distributed_table',
  'create_reference_table',
  'citus_add_local_table_to_metadata',
  'alter_distributed_table',
  'undistribute_table',
  'citus_schema_distribute',
  'citus_schema_move',
  'create_time_partitions',
  'drop_old_time_partitions',
  'alter_old_partitions_set_access_method',
  'isolate_tenant_to_new_shard',
  'citus_add_node',
  'citus_add_inactive_node',
  'citus_activate_node',
  'citus_rebalance_start',
  'citus_rebalance_status',
  'citus_drain_node',
  'citus_move_shard_placement',
  'citus_cluster_changes_block'
])
ORDER BY p.proname, arguments;

\echo '=== Citus settings exposed by this server ==='
SELECT name, setting, unit, context, source, pending_restart,
       short_desc
FROM pg_settings
WHERE name LIKE 'citus.%'
ORDER BY name;

\echo '=== Relevant PostgreSQL settings ==='
SELECT name, setting, unit, context, source, pending_restart
FROM pg_settings
WHERE name = ANY (ARRAY[
  'max_connections',
  'superuser_reserved_connections',
  'shared_preload_libraries',
  'wal_level',
  'max_wal_senders',
  'max_replication_slots',
  'max_prepared_transactions',
  'track_io_timing',
  'enable_partition_pruning',
  'password_encryption',
  'ssl'
])
ORDER BY name;

\echo '=== Citus/partition/statistics relations visible in this database ==='
SELECT n.nspname AS schema_name,
       c.relname AS relation_name,
       c.relkind
FROM pg_class AS c
JOIN pg_namespace AS n ON n.oid = c.relnamespace
WHERE c.relname = ANY (ARRAY[
  'pg_dist_node',
  'pg_dist_partition',
  'pg_dist_shard',
  'pg_dist_placement',
  'pg_dist_colocation',
  'citus_nodes',
  'citus_tables',
  'citus_shards',
  'citus_stat_statements',
  'citus_stat_counters',
  'citus_stat_tenants',
  'citus_stat_activity',
  'citus_dist_stat_activity',
  'citus_lock_waits',
  'time_partitions',
  'pg_stat_statements'
])
ORDER BY c.relname, n.nspname;

\echo '=== Table access methods ==='
SELECT amname, amtype
FROM pg_am
ORDER BY amname;
