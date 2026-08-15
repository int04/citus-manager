\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== Logical Citus table inventory ==='
SELECT p.logicalrelid::regclass AS table_name,
       p.partmethod,
       pg_get_expr(p.partkey, p.logicalrelid) AS distribution_expression,
       p.colocationid,
       p.repmodel,
       c.relkind,
       am.amname AS access_method,
       pg_total_relation_size(p.logicalrelid) AS coordinator_relation_bytes
FROM pg_dist_partition AS p
JOIN pg_class AS c ON c.oid = p.logicalrelid
LEFT JOIN pg_am AS am ON am.oid = c.relam
ORDER BY p.logicalrelid::regclass::text;

\echo '=== High-level citus_tables view when available ==='
SELECT 'SELECT * FROM citus_tables ORDER BY table_name;'
WHERE to_regclass('citus_tables') IS NOT NULL
\gexec

\echo '=== Colocation groups ==='
SELECT colocationid,
       shardcount,
       replicationfactor,
       distributioncolumntype::regtype AS distribution_column_type,
       distributioncolumncollation::regcollation AS distribution_collation
FROM pg_dist_colocation
ORDER BY colocationid;

\echo '=== Tables per colocation group ==='
SELECT p.colocationid,
       count(*) AS table_count,
       string_agg(p.logicalrelid::regclass::text, ', ' ORDER BY p.logicalrelid::regclass::text) AS tables
FROM pg_dist_partition AS p
GROUP BY p.colocationid
ORDER BY p.colocationid;

\echo '=== Primary, unique, and foreign-key definitions for Citus tables ==='
SELECT con.conrelid::regclass AS table_name,
       con.conname,
       con.contype,
       con.confrelid::regclass AS referenced_table,
       con.convalidated,
       pg_get_constraintdef(con.oid) AS definition
FROM pg_constraint AS con
WHERE con.conrelid IN (SELECT logicalrelid FROM pg_dist_partition)
   OR con.confrelid IN (SELECT logicalrelid FROM pg_dist_partition)
ORDER BY table_name::text, con.contype, con.conname;

\echo '=== Replica identity for Citus tables ==='
SELECT c.oid::regclass AS table_name,
       c.relkind,
       c.relreplident,
       i.indexrelid::regclass AS replica_identity_index
FROM pg_class AS c
LEFT JOIN pg_index AS i
  ON i.indrelid = c.oid
 AND i.indisreplident
WHERE c.oid IN (SELECT logicalrelid FROM pg_dist_partition)
ORDER BY table_name::text;
