\set ON_ERROR_STOP on
\pset pager off
\pset null '<NULL>'
\echo '=== Authentication and TLS settings ==='
SELECT name, setting, source, pending_restart
FROM pg_settings
WHERE name = ANY (ARRAY[
  'password_encryption',
  'ssl',
  'ssl_ca_file',
  'ssl_cert_file',
  'ssl_key_file',
  'hba_file',
  'citus.node_conninfo'
])
ORDER BY name;

\echo '=== Login and privilege-sensitive roles ==='
SELECT rolname, rolcanlogin, rolsuper,
       rolcreaterole, rolcreatedb, rolreplication,
       rolbypassrls, rolconnlimit
FROM pg_roles
ORDER BY rolsuper DESC, rolcreaterole DESC, rolname;

\echo '=== Inter-node credential presence without secret contents ==='
SELECT 'SELECT nodeid, rolename, '
       || 'CASE WHEN authinfo IS NULL OR authinfo = '''' '
       || 'THEN ''empty'' ELSE ''configured'' END AS credential_status '
       || 'FROM pg_dist_authinfo ORDER BY nodeid, rolename;'
WHERE to_regclass('pg_dist_authinfo') IS NOT NULL
\gexec

\echo '=== Citus node addresses and active state ==='
SELECT nodeid, groupid, nodename, nodeport,
       noderole, isactive, hasmetadata, metadatasynced
FROM pg_dist_node
ORDER BY groupid, nodeid;

\echo '=== Public schema privileges ==='
SELECT n.nspname AS schema_name,
       r.rolname AS role_name,
       has_schema_privilege(r.rolname, n.oid, 'USAGE') AS has_usage,
       has_schema_privilege(r.rolname, n.oid, 'CREATE') AS has_create
FROM pg_namespace AS n
CROSS JOIN pg_roles AS r
WHERE n.nspname = 'public'
  AND r.rolcanlogin
ORDER BY r.rolname;

\echo '=== Row-level security state on application tables ==='
SELECT n.nspname AS schema_name,
       c.relname AS table_name,
       c.relrowsecurity,
       c.relforcerowsecurity
FROM pg_class AS c
JOIN pg_namespace AS n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p')
  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
ORDER BY n.nspname, c.relname;
