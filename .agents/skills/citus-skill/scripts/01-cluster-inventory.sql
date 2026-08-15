\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== Citus node metadata ==='
SELECT nodeid, groupid, nodename, nodeport,
       noderack, hasmetadata, isactive,
       noderole, nodecluster,
       shouldhaveshards, metadatasynced
FROM pg_dist_node
ORDER BY groupid, nodeid;

\echo '=== Node-role summary ==='
SELECT noderole,
       isactive,
       shouldhaveshards,
       hasmetadata,
       metadatasynced,
       count(*) AS node_count
FROM pg_dist_node
GROUP BY noderole, isactive, shouldhaveshards,
         hasmetadata, metadatasynced
ORDER BY noderole, isactive DESC;

\echo '=== Human-readable citus_nodes view when available ==='
SELECT 'TABLE citus_nodes;'
WHERE to_regclass('citus_nodes') IS NOT NULL
\gexec

\echo '=== Coordinator/control metadata row candidates ==='
SELECT nodeid, groupid, nodename, nodeport,
       noderole, hasmetadata, metadatasynced
FROM pg_dist_node
WHERE groupid = 0
   OR noderole::text <> 'primary'
ORDER BY nodeid;

\echo '=== Current database extension state ==='
SELECT extname, extversion, extnamespace::regnamespace AS extension_schema
FROM pg_extension
WHERE extname IN ('citus', 'columnar', 'pg_stat_statements', 'pg_cron')
ORDER BY extname;
