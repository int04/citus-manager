\set ON_ERROR_STOP on
\pset pager off

-- DESTRUCTIVE: removes only large synthetic expansion schema.
DROP SCHEMA IF EXISTS citus_scale CASCADE;
