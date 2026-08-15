---
name: citus-engineering
description: "Design, review, migrate, operate, troubleshoot, and optimize PostgreSQL Citus. Use for row- or schema-based sharding, local/reference/distributed tables, distribution keys, colocation, shard counts, PostgreSQL partitioning with Citus, time-series layouts, columnar storage, query routing, DML, distributed transactions, worker lifecycle, rebalancing, metadata, observability, HA/backup/upgrade planning, and Citus incidents. Detect installed capabilities before acting. Do not use for plain PostgreSQL work with no Citus or distributed-design component."
---

# Citus Engineering

Use this skill as a version-aware, infrastructure-neutral engineering playbook for PostgreSQL Citus. Apply it to any repository, language, cloud, container platform, or self-managed environment.

The skill must produce decisions that are:

- based on measured workload and actual database metadata;
- explicit about assumptions, constraints, and trade-offs;
- compatible with the installed PostgreSQL and Citus versions;
- safe to execute through staged checkpoints;
- testable with clear pass/fail criteria;
- reversible whenever a realistic rollback path exists.

## Core mission

Help the user:

1. decide whether Citus is appropriate at all;
2. select the correct sharding and table model;
3. design distribution keys, colocation groups, constraints, shard counts, and partitions;
4. preserve efficient query routing and transaction locality;
5. choose row, columnar, or hybrid storage intentionally;
6. scale workers and move shards safely;
7. diagnose failures from evidence rather than guesswork;
8. write migrations, performance experiments, runbooks, and architecture decisions that can be reviewed by another engineer.

## Scope

Use this skill for:

- row-based sharding by distribution column;
- schema-based sharding when supported;
- local, Citus-managed local, reference, distributed, schema, heap, partitioned, and columnar tables;
- distribution-key and colocation design;
- primary keys, unique constraints, foreign keys, replica identity, and sequence strategy;
- shard-count and capacity planning;
- PostgreSQL declarative partitioning inside a Citus design;
- time-series retention, partition lifecycle, and hot/cold storage;
- single-shard, colocated, reference-table, multi-shard, repartition, and coordinator-heavy queries;
- DDL, DML, COPY, INSERT…SELECT, upsert, rollups, and distributed transactions;
- connection fan-out, executor behavior, indexes, statistics, vacuum, and skew;
- worker add/activate/rebalance/drain/move/remove operations;
- hot-tenant isolation and placement management;
- metadata, query statistics, lock, connection, WAL, replication-slot, and prepared-transaction diagnostics;
- migration from plain PostgreSQL or an existing Citus design;
- HA, backup, restore, security, and upgrade planning where Citus changes the requirements;
- incident response and root-cause analysis.

Do not automatically take responsibility for:

- generic PostgreSQL tuning that has no distributed/Citus dimension;
- provider control-plane operations that cannot be performed through SQL;
- a complete organization-specific HA/DR or security program;
- destructive production execution without verified scope, backup, validation, and rollback ownership.

## Non-negotiable rules

1. **Do not hard-code a project.** Use placeholders such as `<SCHEMA>.<TABLE>`, `<DIST_COLUMN>`, `<PARTITION_COLUMN>`, `<WORKER_HOST>`, `<WORKER_PORT>`, `<SHARD_COUNT>`, and `<TENANT_VALUE>` until real values are verified.
2. **Do not hard-code a Citus version.** Inspect `citus_version()`, extension versions, available functions, function signatures, views, and GUCs before using version-sensitive behavior.
3. **Read before writing.** Begin with metadata, plans, sizes, constraints, connections, locks, jobs, and workload evidence.
4. **Separate facts, assumptions, and recommendations.** Never present an inferred topology, workload, or data invariant as a fact.
5. **Do not assume more shards are better.** More shards can improve placement flexibility and parallelism, but also increase planning, relation, metadata, connection, maintenance, and rebalance overhead.
6. **Do not assume PostgreSQL partitioning replaces Citus sharding.** Sharding distributes data across nodes; partitioning divides data within each logical distributed relation, usually for pruning and lifecycle management.
7. **Do not use timestamp as a default hash distribution key.** For time-series designs, normally distribute by a stable tenant/entity key and range-partition by time.
8. **Do not colocate unrelated tables.** Colocation is an operational and data-movement dependency, not merely a matching data type.
9. **Do not remove a node with placements.** Drain it and verify zero placements before removal or infrastructure deletion.
10. **Do not assume a newly added worker receives existing shards.** Verify the plan and rebalance when existing data must move.
11. **Do not expose secrets.** Never print `pg_dist_authinfo.authinfo`, passwords, connection strings, TLS private keys, or secret-file contents.
12. **Do not use manual propagation helpers casually.** Functions that bypass normal coordinator planning or consistency checks are last-resort tools and require a bounded target, idempotency analysis, and validation.
13. **Do not execute destructive conversion steps without data validation.** This includes truncating residual local rows, undistributing tables, changing a distribution column, changing shard count, moving schemas, converting storage methods, and dropping partitions.
14. **Do not treat drain or rebalance as backup.** Data movement is not an independent recovery copy.
15. **Prefer the smallest measurable change.** Each recommendation needs a baseline, hypothesis, controlled change, measurement, and acceptance threshold.
16. **Use checkpoints.** Never return one uninterrupted production command chain for a data- or topology-changing operation.

## Risk classes

Label commands and plans consistently:

| Class | Meaning | Required treatment |
|---|---|---|
| `READ` | Metadata or diagnostic query | Safe by default, but warn when it is expensive or opens many connections |
| `SESSION` | Changes only the current session | State the session scope and reset behavior |
| `WRITE` | Persistent metadata, schema, role, or configuration change | Require preflight and verification |
| `IMPACT` | Moves data, changes routing, locks writes, or changes topology | Require staged execution, capacity checks, monitoring, and rollback/abort criteria |
| `DESTRUCTIVE` | Can delete, detach, truncate, overwrite, or make recovery difficult | Require explicit confirmation, tested backup/restore, validation, and named rollback owner |

## Mandatory workflow

### Step 1 — classify the task

Determine whether the request is primarily:

- architecture or schema design;
- migration;
- partitioning/time-series lifecycle;
- query or performance optimization;
- DML/transaction design;
- capacity planning;
- cluster operation;
- security/HA/backup/upgrade planning;
- troubleshooting or incident response;
- command lookup.

Read only the relevant references. Do not load every file automatically.

### Step 2 — establish whether Citus is necessary

Before distributing data, compare at least these options:

1. plain PostgreSQL with correct indexes and query design;
2. plain PostgreSQL with declarative partitioning;
3. single-node Citus for future compatibility or columnar use;
4. multi-node Citus row-based sharding;
5. multi-node Citus schema-based sharding;
6. a separated workload or service boundary rather than one distributed schema.

Recommend Citus only when the scaling, isolation, throughput, storage, or operational requirement justifies distributed complexity.

### Step 3 — gather evidence

From the repository and/or database, determine:

- PostgreSQL and Citus versions on every relevant node;
- deployment type: self-managed, managed service, or unknown;
- coordinator/query-node behavior and worker topology;
- schema, migrations, dependencies, extensions, and largest tables;
- table types, distribution columns, partition keys, shard counts, colocation groups, and access methods;
- primary/unique/foreign-key constraints and replica identity;
- hot queries, parameters, joins, filters, groupings, sort order, and transaction boundaries;
- read/write ratio, ingest rate, retention, growth, concurrency, and latency objectives;
- CPU, memory, disk, network, `max_connections`, WAL, slots, and backup constraints;
- shard, tenant, partition, and node skew;
- how applications connect and pool connections;
- the intended future scale, not only the current cluster.

When database access is unavailable, generate a read-only evidence-collection plan. Never invent results.

### Step 4 — detect installed capabilities

Start with:

```sql
SELECT version();
SELECT citus_version();

SELECT extname, extversion
FROM pg_extension
WHERE extname IN ('citus', 'pg_stat_statements', 'pg_cron');

SELECT n.nspname AS schema_name,
       p.proname AS function_name,
       pg_get_function_identity_arguments(p.oid) AS arguments,
       pg_get_function_result(p.oid) AS result_type
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname IN (
  'create_distributed_table',
  'alter_distributed_table',
  'create_reference_table',
  'citus_add_local_table_to_metadata',
  'citus_schema_distribute',
  'create_time_partitions',
  'drop_old_time_partitions',
  'alter_old_partitions_set_access_method',
  'citus_rebalance_start',
  'citus_drain_node',
  'citus_cluster_changes_block',
  'citus_create_restore_point'
)
ORDER BY p.proname, arguments;
```

Inspect available Citus GUCs rather than assuming names or defaults:

```sql
SELECT name, setting, unit, context, source
FROM pg_settings
WHERE name LIKE 'citus.%'
ORDER BY name;
```

When a function, view, or GUC is absent, choose a compatible approach or state the upgrade requirement. Do not force a copied signature with arbitrary casts.

### Step 5 — choose the design in the correct order

Use this hierarchy:

1. **Workload boundary:** what data must scale or remain transactionally local?
2. **Sharding model:** no sharding, row-based, or schema-based.
3. **Table classification:** local, managed local, reference, distributed, schema, partitioned, or columnar.
4. **Distribution domain:** tenant/entity/key that defines data locality.
5. **Colocation groups:** tables that truly share joins and transaction boundaries.
6. **Integrity model:** primary/unique keys, foreign keys, replica identity, id generation.
7. **Shard count and capacity:** current and future workers, cores, data volume, connections, and maintenance cost.
8. **Partitioning:** only when pruning, retention, archival, index/vacuum isolation, or operational lifecycle justify it.
9. **Storage method:** heap, columnar, or hybrid hot/cold.
10. **Query and transaction shape:** preserve routing key and minimize cross-node work.
11. **Operational lifecycle:** rebalance, backup, failure, upgrade, and observability.

Do not start from a favorite command such as `create_distributed_table()` and work backward.

### Step 6 — model partitioning as a second dimension

For a common time-series design:

- hash-distribute by a stable tenant/entity key;
- range-partition by event time;
- include the distribution key in hot queries for shard pruning;
- include the time predicate for partition pruning;
- size the partition interval from ingest volume, query windows, retention, index/vacuum cost, lock budget, and relation-count overhead;
- pre-create future partitions;
- verify retention and archival jobs;
- avoid unsupported or operationally explosive multi-level partition trees;
- evaluate the product `shard_count × active_partition_count × index_count` before deployment.

Read `references/03-partitioning-and-time-series.md` for the full method.

### Step 7 — classify the query path

Classify each important query as:

1. single-shard/router;
2. colocated distributed;
3. distributed plus reference table;
4. multi-shard parallel with bounded intermediate data;
5. repartition/cross-shard;
6. coordinator-heavy or push-pull;
7. unsupported/version-sensitive.

Optimize in that order. Reduce shards/tasks and intermediate data before increasing executor parallelism.

### Step 8 — create a staged plan

Every `WRITE`, `IMPACT`, or `DESTRUCTIVE` plan must contain:

1. purpose and success criteria;
2. confirmed facts and explicit assumptions;
3. read-only preflight;
4. capacity and lock analysis;
5. staging rehearsal;
6. phase-by-phase commands with the node/database on which each runs;
7. checkpoint after every phase;
8. live metrics to watch;
9. abort conditions;
10. validation queries;
11. rollback or forward-recovery path;
12. cleanup only after completion criteria are met.

## Reference routing

| Task | Read |
|---|---|
| Architecture, terminology, capability detection | `references/01-architecture-and-capability-model.md` |
| Table types, sharding models, distribution keys, colocation, constraints, shard count | `references/02-data-modeling.md` |
| PostgreSQL partitioning, time series, retention, partition automation | `references/03-partitioning-and-time-series.md` |
| Query routing, EXPLAIN, indexes, statistics, vacuum, connections, skew, performance | `references/04-query-and-performance-optimization.md` |
| INSERT/COPY/upsert/UPDATE/DELETE, transactions, rollups, distributed functions | `references/05-dml-transactions-and-ingestion.md` |
| Columnar and hybrid row/columnar storage | `references/06-columnar-and-hybrid-storage.md` |
| Add/rebalance/drain/remove, table conversion, hot tenants, schema moves | `references/07-cluster-operations.md` |
| Metadata, monitoring, security, HA, backup, restore, and upgrades | `references/08-observability-security-ha-and-upgrades.md` |
| Greenfield patterns and migration from PostgreSQL/Citus | `references/09-migrations-and-architecture-patterns.md` |
| Error investigation and incident diagnosis | `references/10-troubleshooting.md` |
| Citus SQL/UDF/GUC quick reference | `references/11-command-reference.md` |
| Decision trees, review gates, production checklists | `references/12-decision-trees-and-checklists.md` |
| Source map and version policy | `references/13-official-sources-and-version-policy.md` |
| Advanced SQL, analytics, joins, views, triggers, RLS, extensions, compatibility | `references/14-advanced-sql-analytics-and-extensions.md` |

Use the read-only SQL in `scripts/` when deterministic evidence collection is useful. Use `scripts/capacity_model.py` for explicit upper-bound shard, partition, relation, and connection arithmetic; treat its output as a planning estimate that still requires runtime evidence and benchmarks. Use templates in `assets/` for architecture reviews, ADRs, migrations, experiments, and incidents.

## Output contract

For a technical Citus response, prefer this structure:

1. **Decision** — the recommended direction and why.
2. **Known facts** — evidence from the repository, database, or user.
3. **Assumptions and unknowns** — what still needs verification.
4. **Design analysis** — alternatives, trade-offs, and rejected options.
5. **Read-only checks** — exact SQL and where to run it.
6. **Implementation plan** — phases and commands.
7. **Validation** — measurable pass/fail criteria.
8. **Risks and rollback** — mandatory for data, schema, storage, or topology changes.
9. **Version note** — features or signatures that require capability confirmation.

For a command lookup, provide:

- purpose;
- risk class;
- required privileges;
- node/database on which to run it;
- capability check;
- template command;
- validation command;
- major caveats.

## Standard placeholders

| Placeholder | Meaning |
|---|---|
| `<DB_NAME>` | Database in which the Citus extension is enabled |
| `<SCHEMA>.<TABLE>` | Fully qualified logical table name |
| `<PARENT_TABLE>` | Partitioned parent table |
| `<DIST_COLUMN>` | Distribution column |
| `<PARTITION_COLUMN>` | PostgreSQL partition key |
| `<COLOCATED_TABLE>` | Root table of a colocation group |
| `<SHARD_COUNT>` | Target shard count |
| `<PARTITION_INTERVAL>` | Time or domain interval for partitions |
| `<COORDINATOR_HOST>` | Control/coordinator endpoint |
| `<WORKER_HOST>` | Worker DNS name or private address |
| `<WORKER_PORT>` | PostgreSQL worker port |
| `<TENANT_VALUE>` | Distribution-key value used for routing analysis |
| `<SHARD_ID>` | Logical shard identifier |
| `<NODE_ID>` | Citus node identifier |
| `<RETENTION_INTERVAL>` | Data-retention boundary |

Never tell the user to execute a placeholder literally. Replace it with verified values or clearly mark the command as a template.

## Completion standard

A recommendation is incomplete unless another engineer can answer:

- Why this model or command was chosen.
- What evidence supports it.
- What changes physically and logically.
- Which queries become single-shard or remain multi-shard.
- How shard, partition, connection, and relation counts scale.
- How integrity is enforced.
- What is monitored during execution.
- How success is verified.
- How to stop, roll back, or recover.
