\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== Shard placement inventory requires citus_shards ==='
SELECT CASE
         WHEN to_regclass('citus_shards') IS NULL
           THEN 'citus_shards is not available; inspect pg_dist_shard and pg_dist_placement for this version'
         ELSE 'citus_shards is available'
       END AS status;

\echo '=== Bytes and placements by node ==='
SELECT $q$
SELECT nodename, nodeport,
       count(*) AS placement_count,
       coalesce(sum(shard_size), 0)::bigint AS total_bytes,
       coalesce(avg(shard_size), 0)::bigint AS average_shard_bytes,
       coalesce(max(shard_size), 0)::bigint AS largest_shard_bytes
FROM citus_shards
GROUP BY nodename, nodeport
ORDER BY total_bytes DESC, nodename, nodeport;
$q$
WHERE to_regclass('citus_shards') IS NOT NULL
\gexec

\echo '=== Size skew by logical table ==='
SELECT $q$
SELECT table_name,
       count(DISTINCT shardid) AS logical_shards,
       min(shard_size)::bigint AS smallest_placement_bytes,
       avg(shard_size)::bigint AS average_placement_bytes,
       max(shard_size)::bigint AS largest_placement_bytes,
       round(
         max(shard_size)::numeric /
         NULLIF(avg(shard_size)::numeric, 0),
         2
       ) AS max_to_average_ratio
FROM citus_shards
GROUP BY table_name
ORDER BY max_to_average_ratio DESC NULLS LAST, table_name;
$q$
WHERE to_regclass('citus_shards') IS NOT NULL
\gexec

\echo '=== Largest shard placements ==='
SELECT $q$
SELECT table_name, shardid, nodename, nodeport, shard_size
FROM citus_shards
ORDER BY shard_size DESC NULLS LAST
LIMIT 100;
$q$
WHERE to_regclass('citus_shards') IS NOT NULL
\gexec

\echo '=== Placements on nodes that should not hold shards ==='
SELECT $q$
SELECT n.nodeid, n.nodename, n.nodeport,
       n.shouldhaveshards,
       count(s.shardid) AS placement_count,
       coalesce(sum(s.shard_size), 0)::bigint AS bytes
FROM pg_dist_node AS n
LEFT JOIN citus_shards AS s
  ON s.nodename = n.nodename
 AND s.nodeport = n.nodeport
WHERE n.shouldhaveshards = false
GROUP BY n.nodeid, n.nodename, n.nodeport, n.shouldhaveshards
ORDER BY bytes DESC;
$q$
WHERE to_regclass('citus_shards') IS NOT NULL
\gexec
