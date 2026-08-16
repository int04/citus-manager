\set ON_ERROR_STOP on
\pset pager off

-- DESTRUCTIVE: removes only synthetic schemas created by citus_demo_seed.sql.
DROP SCHEMA IF EXISTS citus_demo_tenant_blue CASCADE;
DROP SCHEMA IF EXISTS citus_demo CASCADE;
