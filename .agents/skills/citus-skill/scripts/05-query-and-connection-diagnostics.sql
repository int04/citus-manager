\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== Active and waiting sessions ==='
SELECT pid, usename, datname, application_name, client_addr,
       state, wait_event_type, wait_event,
       backend_start, xact_start, query_start,
       left(query, 300) AS query
FROM pg_stat_activity
WHERE pid <> pg_backend_pid()
ORDER BY query_start NULLS LAST, pid;

\echo '=== Connection counts ==='
SELECT datname, usename, application_name, client_addr, state,
       count(*) AS connections
FROM pg_stat_activity
GROUP BY datname, usename, application_name, client_addr, state
ORDER BY connections DESC, datname, usename;

\echo '=== Long transactions ==='
SELECT pid, usename, application_name, client_addr,
       now() - xact_start AS transaction_age,
       state, wait_event_type, wait_event,
       left(query, 300) AS query
FROM pg_stat_activity
WHERE xact_start IS NOT NULL
  AND pid <> pg_backend_pid()
ORDER BY xact_start;

\echo '=== PostgreSQL lock waits ==='
SELECT blocked.pid AS blocked_pid,
       blocked.usename AS blocked_user,
       blocking.pid AS blocking_pid,
       blocking.usename AS blocking_user,
       blocked.wait_event_type,
       blocked.wait_event,
       left(blocked.query, 250) AS blocked_query,
       left(blocking.query, 250) AS blocking_query
FROM pg_stat_activity AS blocked
CROSS JOIN LATERAL unnest(pg_blocking_pids(blocked.pid)) AS blocker(blocking_pid)
JOIN pg_stat_activity AS blocking ON blocking.pid = blocker.blocking_pid
ORDER BY blocked.query_start;

\echo '=== Citus lock waits when available ==='
SELECT 'TABLE citus_lock_waits;'
WHERE to_regclass('citus_lock_waits') IS NOT NULL
\gexec

\echo '=== Top pg_stat_statements by total execution time when available ==='
SELECT $q$
SELECT queryid, calls, total_exec_time, mean_exec_time, rows,
       shared_blks_hit, shared_blks_read,
       temp_blks_read, temp_blks_written,
       left(query, 300) AS query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 50;
$q$
WHERE to_regclass('pg_stat_statements') IS NOT NULL
\gexec

\echo '=== Citus query statistics when available ==='
SELECT 'SELECT * FROM citus_stat_statements ORDER BY calls DESC LIMIT 50;'
WHERE to_regclass('citus_stat_statements') IS NOT NULL
\gexec

\echo '=== Citus single/multi-shard counters when available ==='
SELECT 'TABLE citus_stat_counters;'
WHERE to_regclass('citus_stat_counters') IS NOT NULL
\gexec

\echo '=== Tenant statistics when available ==='
SELECT 'SELECT * FROM citus_stat_tenants LIMIT 100;'
WHERE to_regclass('citus_stat_tenants') IS NOT NULL
\gexec
