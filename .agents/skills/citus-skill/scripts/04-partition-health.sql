\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== Declaratively partitioned parents ==='
SELECT n.nspname AS schema_name,
       c.relname AS parent_table,
       pg_get_partkeydef(c.oid) AS partition_key,
       count(i.inhrelid) AS direct_partition_count,
       pg_total_relation_size(c.oid) AS parent_relation_bytes
FROM pg_partitioned_table AS pt
JOIN pg_class AS c ON c.oid = pt.partrelid
JOIN pg_namespace AS n ON n.oid = c.relnamespace
LEFT JOIN pg_inherits AS i ON i.inhparent = c.oid
GROUP BY n.nspname, c.relname, c.oid
ORDER BY n.nspname, c.relname;

\echo '=== Direct partition bounds and sizes ==='
SELECT parent.oid::regclass AS parent_table,
       child.oid::regclass AS partition_table,
       pg_get_expr(child.relpartbound, child.oid) AS partition_bound,
       am.amname AS access_method,
       pg_total_relation_size(child.oid) AS total_bytes,
       stats.n_live_tup,
       stats.n_dead_tup,
       stats.last_autovacuum,
       stats.last_autoanalyze
FROM pg_inherits AS i
JOIN pg_class AS parent ON parent.oid = i.inhparent
JOIN pg_class AS child ON child.oid = i.inhrelid
LEFT JOIN pg_am AS am ON am.oid = child.relam
LEFT JOIN pg_stat_all_tables AS stats ON stats.relid = child.oid
ORDER BY parent_table::text, partition_table::text;

\echo '=== Partitioned Citus parents ==='
SELECT p.logicalrelid::regclass AS table_name,
       pg_get_expr(p.partkey, p.logicalrelid) AS distribution_expression,
       p.colocationid,
       pg_get_partkeydef(p.logicalrelid) AS postgres_partition_key
FROM pg_dist_partition AS p
JOIN pg_partitioned_table AS pt ON pt.partrelid = p.logicalrelid
ORDER BY table_name::text;

\echo '=== Invalid indexes in partition trees ==='
SELECT t.oid::regclass AS table_name,
       idx.indexrelid::regclass AS index_name,
       idx.indisvalid,
       idx.indisready,
       idx.indislive
FROM pg_index AS idx
JOIN pg_class AS t ON t.oid = idx.indrelid
WHERE NOT idx.indisvalid
   OR NOT idx.indisready
   OR NOT idx.indislive
ORDER BY table_name::text, index_name::text;

\echo '=== Citus time_partitions helper view when available ==='
SELECT 'TABLE time_partitions;'
WHERE to_regclass('time_partitions') IS NOT NULL
\gexec
