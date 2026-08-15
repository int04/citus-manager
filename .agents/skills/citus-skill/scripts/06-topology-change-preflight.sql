\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== Version and extension preflight ==='
SELECT version();
SELECT 'SELECT citus_version();'
WHERE to_regprocedure('citus_version()') IS NOT NULL
\gexec
SELECT extname, extversion FROM pg_extension ORDER BY extname;

\echo '=== Node state ==='
SELECT nodeid, groupid, nodename, nodeport,
       noderole, isactive, shouldhaveshards,
       hasmetadata, metadatasynced
FROM pg_dist_node
ORDER BY groupid, nodeid;

\echo '=== Placement and byte totals by node ==='
SELECT $q$
SELECT nodename, nodeport,
       count(*) AS placements,
       coalesce(sum(shard_size), 0)::bigint AS bytes
FROM citus_shards
GROUP BY nodename, nodeport
ORDER BY bytes DESC;
$q$
WHERE to_regclass('citus_shards') IS NOT NULL
\gexec

\echo '=== Nodes already marked not to hold shards ==='
SELECT nodeid, nodename, nodeport, isactive, shouldhaveshards
FROM pg_dist_node
WHERE shouldhaveshards = false
ORDER BY nodeid;

\echo '=== Tables without a primary key or replica-identity index ==='
WITH distributed AS (
  SELECT logicalrelid
  FROM pg_dist_partition
), identity_state AS (
  SELECT c.oid,
         c.relreplident,
         bool_or(coalesce(i.indisprimary, false)) AS has_primary_key,
         bool_or(coalesce(i.indisreplident, false)) AS has_replica_identity_index
  FROM pg_class AS c
  LEFT JOIN pg_index AS i ON i.indrelid = c.oid
  WHERE c.oid IN (SELECT logicalrelid FROM distributed)
  GROUP BY c.oid, c.relreplident
)
SELECT oid::regclass AS table_name,
       relreplident,
       has_primary_key,
       has_replica_identity_index
FROM identity_state
WHERE NOT has_primary_key
  AND relreplident <> 'f'
  AND NOT has_replica_identity_index
ORDER BY table_name::text;

\echo '=== Replication slots and retained WAL ==='
SELECT slot_name, slot_type, database, active,
       restart_lsn, confirmed_flush_lsn,
       CASE WHEN restart_lsn IS NULL THEN NULL
            ELSE pg_size_pretty(
              pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)
            )
       END AS retained_wal
FROM pg_replication_slots
ORDER BY slot_name;

\echo '=== Prepared transactions ==='
SELECT transaction, gid, prepared, owner, database
FROM pg_prepared_xacts
ORDER BY prepared;

\echo '=== Distributed transaction metadata when available ==='
SELECT 'TABLE pg_dist_transaction;'
WHERE to_regclass('pg_dist_transaction') IS NOT NULL
\gexec

\echo '=== Long transactions and blockers ==='
SELECT pid, usename, application_name,
       now() - xact_start AS transaction_age,
       wait_event_type, wait_event,
       left(query, 300) AS query
FROM pg_stat_activity
WHERE xact_start IS NOT NULL
  AND pid <> pg_backend_pid()
ORDER BY xact_start;

\echo '=== Rebalance plan when the plan function exists ==='
SELECT 'SELECT * FROM get_rebalance_table_shards_plan();'
WHERE to_regprocedure('get_rebalance_table_shards_plan()') IS NOT NULL
\gexec
