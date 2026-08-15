# Official Sources and Version Policy

This skill is version-aware, not version-locked. The connected database, current official documentation, and managed-service control plane are the sources of truth.

Documentation baseline reviewed: 2026-08-15.

## 1. Runtime source of truth

Before using a Citus function, view, or GUC:

```sql
SELECT version();
SELECT citus_version();

SELECT extname, extversion
FROM pg_extension
WHERE extname = 'citus';

SELECT n.nspname,
       p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments,
       pg_get_function_result(p.oid) AS result_type
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname = '<FUNCTION_NAME>'
ORDER BY arguments;

SELECT name, setting, context, source
FROM pg_settings
WHERE name = '<GUC_NAME>';
```

If runtime and documentation conflict:

1. confirm the node and database;
2. confirm extension and package versions on all nodes;
3. check the target version's release/upgrade notes;
4. check whether a managed service changes the feature;
5. prefer a compatible documented path rather than forcing a signature.

## 2. OpenAI Codex skill format

- Build skills: https://developers.openai.com/codex/build-skills
- Codex best practices: https://developers.openai.com/codex/learn/best-practices

The package uses:

- required `SKILL.md` with `name` and `description` frontmatter;
- optional `references/` for progressive disclosure;
- optional `scripts/` for deterministic read-only diagnostics and planning calculations;
- optional `assets/` for templates;
- optional `agents/openai.yaml` for interface and invocation metadata.

## 3. Citus documentation index and overview

- Documentation home: https://learn.microsoft.com/en-us/postgresql/citus/?view=citus-14
- What is Citus / architecture: https://learn.microsoft.com/en-us/postgresql/citus/what-is-citus?view=citus-14
- Open-source repository: https://github.com/citusdata/citus
- Releases: https://github.com/citusdata/citus/releases
- Changelog source: https://github.com/citusdata/citus/blob/main/CHANGELOG.md

Use these for architecture, sharding models, supported PostgreSQL lines, and current release notes.

## 4. Data modeling and migration

- Choosing a distribution column and colocation: https://learn.microsoft.com/en-us/postgresql/citus/data-modeling?view=citus-14
- Table management: https://learn.microsoft.com/en-us/postgresql/citus/table-management?view=citus-14
- Multi-tenant tutorial: https://learn.microsoft.com/en-us/postgresql/citus/tutorial-multi-tenant?view=citus-14
- Identify a distribution strategy: https://learn.microsoft.com/en-us/postgresql/citus/migrate/migration-schema?view=citus-14
- Prepare an application: https://learn.microsoft.com/en-us/postgresql/citus/migrate/migration-query?view=citus-14
- Migration section: https://learn.microsoft.com/en-us/postgresql/citus/migrate/?view=citus-14

Use these for table classification, distribution-key choice, application query changes, and schema migration.

## 5. Partitioning, time series, and PostgreSQL behavior

- Citus time-series tutorial: https://learn.microsoft.com/en-us/postgresql/citus/tutorial-time-series?view=citus-14
- PostgreSQL declarative partitioning: https://www.postgresql.org/docs/current/ddl-partitioning.html
- PostgreSQL `CREATE TABLE`: https://www.postgresql.org/docs/current/sql-createtable.html
- PostgreSQL `ALTER TABLE`: https://www.postgresql.org/docs/current/sql-altertable.html
- PostgreSQL query-planning settings: https://www.postgresql.org/docs/current/runtime-config-query.html

Use PostgreSQL documentation for partition bounds, pruning, constraints, attach/detach locks, and planner behavior. Use Citus documentation for distributed parent/partition behavior and helper functions.

## 6. Citus API and metadata

- API index: https://learn.microsoft.com/en-us/postgresql/citus/api?view=citus-14
- Utility functions: https://learn.microsoft.com/en-us/postgresql/citus/api-udf?view=citus-14
- GUC parameters: https://learn.microsoft.com/en-us/postgresql/citus/api-guc?view=citus-14
- Metadata tables and views: https://learn.microsoft.com/en-us/postgresql/citus/api-metadata?view=citus-14

Use the utility reference for exact target-version signatures, privileges, arguments, and return types. Still verify with `pg_proc` because installed patch level and provider exposure can differ.

## 7. SQL, DDL, DML, and query processing

- Distributed DDL: https://learn.microsoft.com/en-us/postgresql/citus/reference-ddl?view=citus-14
- Distributed DML: https://learn.microsoft.com/en-us/postgresql/citus/reference-dml?view=citus-14
- SQL queries: https://learn.microsoft.com/en-us/postgresql/citus/reference-sql?view=citus-14
- SQL support and workarounds: https://learn.microsoft.com/en-us/postgresql/citus/reference-workarounds?view=citus-14
- Manual query propagation: https://learn.microsoft.com/en-us/postgresql/citus/reference-propagation?view=citus-14
- Efficient rollups with HyperLogLog: https://learn.microsoft.com/en-us/postgresql/citus/efficient-rollup?view=citus-14
- Citus guides index: https://learn.microsoft.com/en-us/postgresql/citus/guides?view=citus-14
- Triggers on distributed tables: https://learn.microsoft.com/en-us/postgresql/citus/triggers?view=citus-14
- External integrations and CDC: https://learn.microsoft.com/en-us/postgresql/citus/integrations?view=citus-14

Manual propagation helpers are advanced last-resort tools. Their official documentation explicitly warns that they can bypass coordinator logic, locking, and consistency checks. Advanced SQL and integration behavior must be tested against the exact table types, query shape, and installed release.

## 8. Performance and capacity

- Query performance tuning: https://learn.microsoft.com/en-us/postgresql/citus/performance-tuning?view=citus-14
- Cluster management and production sizing: https://learn.microsoft.com/en-us/postgresql/citus/cluster-management?view=citus-14
- Citus FAQ: https://learn.microsoft.com/en-us/postgresql/citus/faq-citus?view=citus-14

Shard-count ranges in official material are starting points for benchmark design. They are not universal defaults. Recalculate for worker count, cores, data size, partition count, concurrency, and connection capacity.

## 9. Columnar storage

- Table management / columnar: https://learn.microsoft.com/en-us/postgresql/citus/table-management?view=citus-14
- Time-series hot/cold example: https://learn.microsoft.com/en-us/postgresql/citus/tutorial-time-series?view=citus-14

Columnar limitations are especially version-sensitive. Verify current support for update/delete, indexes, constraints, tuple locks, logical decoding, and serializable transactions before conversion.

## 10. Cluster operations and reliability

- Cluster management: https://learn.microsoft.com/en-us/postgresql/citus/cluster-management?view=citus-14
- Utility functions: https://learn.microsoft.com/en-us/postgresql/citus/api-udf?view=citus-14
- Common errors: https://learn.microsoft.com/en-us/postgresql/citus/common-errors?view=citus-14
- Citus upgrades: https://learn.microsoft.com/en-us/postgresql/citus/upgrade-citus?view=citus-14

Use current release-specific documentation for:

- node addition and clone/snapshot workflows;
- online shard movement modes;
- background rebalance APIs;
- cluster-wide backup/change-block functions;
- restore points;
- upgrade ordering.

## 11. PostgreSQL reference areas

- PostgreSQL current documentation: https://www.postgresql.org/docs/current/
- Constraints: https://www.postgresql.org/docs/current/ddl-constraints.html
- Indexes: https://www.postgresql.org/docs/current/indexes.html
- Vacuuming: https://www.postgresql.org/docs/current/routine-vacuuming.html
- Monitoring: https://www.postgresql.org/docs/current/monitoring.html
- High availability: https://www.postgresql.org/docs/current/high-availability.html
- Backup and restore: https://www.postgresql.org/docs/current/backup.html
- Logical replication: https://www.postgresql.org/docs/current/logical-replication.html

Citus does not remove the need to apply PostgreSQL fundamentals on every worker.

## 12. Managed service policy

When a provider manages Citus:

1. identify the exact product and service tier;
2. use provider documentation for node, backup, HA, TLS, and upgrade operations;
3. detect which Citus UDFs and views remain available;
4. do not instruct the user to bypass the control plane;
5. keep data-model, query-locality, partition, and validation reasoning provider-neutral.

## 13. Source-quality hierarchy

Prefer, in order:

1. installed database metadata and tested behavior;
2. official documentation for the installed release;
3. official release notes, changelog, and source repository;
4. provider documentation for managed behavior;
5. PostgreSQL official documentation;
6. reproducible staging experiments;
7. third-party material only as a lead, never as the sole basis for a production-impacting command.

## 14. How to record a version-sensitive recommendation

Use this format:

```text
Capability: <FUNCTION/BEHAVIOR>
Observed PostgreSQL version: <VALUE>
Observed Citus version: <VALUE>
Observed signature/GUC/view: <VALUE>
Official target-version source: <URL>
Managed-service restriction: <VALUE OR NONE KNOWN>
Staging result: <PASS/FAIL + EVIDENCE>
Production decision: <APPROVED/REJECTED/NEEDS UPGRADE>
```

## 15. Maintenance policy for this repository

For each update:

- review the current Citus documentation family;
- review PostgreSQL partitioning/constraint behavior relevant to the change;
- update the source map;
- preserve runtime capability detection;
- add or update evaluation prompts;
- avoid removing older compatible guidance without a documented reason;
- update `CHANGELOG.md`.
