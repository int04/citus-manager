# Citus Engineering Skill for Codex

A public, reusable Codex skill for designing, reviewing, migrating, operating, troubleshooting, and optimizing PostgreSQL Citus systems from basic table distribution through advanced partitioning, time-series lifecycle, columnar storage, and cluster operations.

The package is deliberately independent of application language, repository layout, operating system, container platform, cloud provider, and fixed PostgreSQL/Citus version. It makes Codex inspect the connected database and installed capabilities before recommending version-sensitive SQL.

## Why this skill exists

Citus performance and correctness depend far more on data locality than on isolated configuration changes. A technically valid command can still create a poor system when the distribution key, colocation group, transaction boundary, shard count, partition layout, or query path is wrong.

This skill gives Codex a disciplined decision process:

1. determine whether Citus is actually required;
2. choose row-based, schema-based, or no sharding;
3. classify every table;
4. design distribution keys and colocation from real queries and transactions;
5. preserve integrity and replica identity;
6. plan shard count and connection capacity;
7. add PostgreSQL partitioning only for a measurable lifecycle or pruning need;
8. choose heap, columnar, or hybrid storage;
9. optimize query routing before executor settings or hardware;
10. perform topology and data movement through checkpoints, validation, and rollback.

## Coverage

### Architecture and data modeling

- Citus coordinator, workers, metadata nodes, placements, and query-from-any-node concepts.
- Row-based sharding, schema-based sharding, and the decision not to shard.
- Local, Citus-managed local, reference, distributed, schema, partitioned, heap, and columnar tables.
- Distribution-key scoring by routing frequency, join locality, transaction locality, cardinality, skew, immutability, and future scale.
- Colocation groups and their query, transaction, rebalance, and failure implications.
- Primary keys, unique constraints, foreign keys, replica identity, and identifier strategy.
- Shard-count and worker-capacity planning with connection-budget checks.

### PostgreSQL partitioning with Citus

- The difference between horizontal Citus sharding and PostgreSQL declarative partitioning.
- Two-dimensional layouts: hash distribution by tenant/entity plus range partitioning by time.
- Range, list, and hash partitioning selection.
- Partition interval design from ingest rate, query windows, retention, maintenance, lock budget, and relation count.
- Partition pruning and shard pruning together.
- Parent/child creation order, constraints, indexes, default partitions, attach/detach, and lock awareness.
- Time partition creation, pre-creation, retention, archival, and automation.
- Relation-count budgeting using shard count × active partitions × indexes.
- Hot row partitions plus cold columnar partitions.
- Unsupported or high-risk multi-level partition layouts.

### Query and performance engineering

- Single-shard/router, colocated, reference-table, multi-shard, repartition, push-pull, and coordinator-heavy paths.
- `EXPLAIN (ANALYZE, VERBOSE, BUFFERS)` interpretation for distributed queries.
- Query rewrites that preserve the distribution key.
- Indexes, partial indexes, GIN, BRIN, statistics, analyze, autovacuum, and bloat.
- Connection fan-out and Citus executor GUCs.
- Shard, partition, tenant, and node skew.
- Benchmark design with controlled baselines and measurable acceptance criteria.

### DML, transactions, and ingestion

- `INSERT`, multi-row insert, `COPY`, upsert, `INSERT ... SELECT`, rollups, `UPDATE`, and `DELETE`.
- Single-shard versus multi-shard transactions and two-phase commit considerations.
- Distributed functions for tenant-scoped multi-statement logic.
- Bulk-load staging and cutover patterns.
- Idempotency, retry behavior, identifier generation, and data-integrity checks.

### Storage and lifecycle

- Heap versus columnar trade-offs.
- Columnar conversion, current limitations, stripe behavior, and bulk-write implications.
- Hybrid hot/cold designs for immutable history.
- Retention, rollups, archival, and partition conversion.

### Advanced SQL and analytics

- Aggregate pushdown, partial aggregation, exact and approximate distinct counts, percentiles, and rollups.
- Global ordering, top-N, keyset pagination, windows, grouping sets, and materialized views.
- Colocated, reference, repartition, push-pull, and outer-join planning.
- Version-sensitive `MERGE`, recursive CTE, row-locking, trigger, sequence, and RLS behavior.
- Extension/object propagation, custom distributed functions, JSONB, faceted search, and DDL fan-out.
- A repeatable compatibility test matrix for advanced PostgreSQL features on Citus.

### Operations and reliability

- Add, activate, rebalance, drain, move, disable, update, and remove nodes.
- Change shard count, distribution column, colocation, table type, and schema placement.
- Isolate and move hot tenants.
- Read-only metadata and health diagnostics.
- Authentication safety, TLS/node connection settings, roles, and secret handling.
- HA topology, backup/PITR coordination, cluster-wide restore points, restore testing, and upgrades.
- Common errors, lock waits, prepared transactions, WAL/replication slots, metadata drift, and failed workers.

## Learning path: basic to advanced

The files are modular, but a new Citus engineer can study them in this order:

1. **Foundation:** architecture, table types, capability detection, and the decision whether to shard at all — `references/01-architecture-and-capability-model.md`.
2. **Core design:** distribution keys, colocation, constraints, replica identity, and shard-count methodology — `references/02-data-modeling.md`.
3. **Query and write paths:** routing, `EXPLAIN`, indexing, connections, DML, ingestion, and transactions — `references/04-query-and-performance-optimization.md` and `references/05-dml-transactions-and-ingestion.md`.
4. **Advanced lifecycle:** declarative partitioning, time series, retention, rollups, and hot/cold columnar storage — `references/03-partitioning-and-time-series.md` and `references/06-columnar-and-hybrid-storage.md`.
5. **Production operations:** workers, rebalance, drain, topology, observability, security, HA, backup, restore, and upgrades — `references/07-cluster-operations.md` and `references/08-observability-security-ha-and-upgrades.md`.
6. **Architecture and expert topics:** migrations, decision trees, advanced SQL, analytics, extensions, CDC, triggers, and compatibility testing — `references/09-migrations-and-architecture-patterns.md`, `references/12-decision-trees-and-checklists.md`, and `references/14-advanced-sql-analytics-and-extensions.md`.

Use `references/11-command-reference.md` as the operational lookup and `references/10-troubleshooting.md` during incidents. The source and version policy is in `references/13-official-sources-and-version-policy.md`.

## Requirements

- Codex CLI, the Codex IDE extension, or another host that supports the OpenAI skill format.
- Python 3.10 or newer only when running the capacity calculator, package validator, or unit tests.
- `psql` and database credentials only when running the optional diagnostic SQL against a Citus-enabled database.
- No Python packages beyond the standard library for local validation and capacity calculations.

## Repository layout

```text
citus-skill/
├── SKILL.md
├── README.md
├── LICENSE
├── CHANGELOG.md
├── CONTRIBUTING.md
├── CODE_OF_CONDUCT.md
├── SECURITY.md
├── .github/
│   ├── workflows/validate.yml
│   ├── ISSUE_TEMPLATE/bug_report.yml
│   ├── ISSUE_TEMPLATE/documentation.yml
│   └── pull_request_template.md
├── agents/
│   └── openai.yaml
├── references/
│   ├── 01-architecture-and-capability-model.md
│   ├── 02-data-modeling.md
│   ├── 03-partitioning-and-time-series.md
│   ├── 04-query-and-performance-optimization.md
│   ├── 05-dml-transactions-and-ingestion.md
│   ├── 06-columnar-and-hybrid-storage.md
│   ├── 07-cluster-operations.md
│   ├── 08-observability-security-ha-and-upgrades.md
│   ├── 09-migrations-and-architecture-patterns.md
│   ├── 10-troubleshooting.md
│   ├── 11-command-reference.md
│   ├── 12-decision-trees-and-checklists.md
│   ├── 13-official-sources-and-version-policy.md
│   └── 14-advanced-sql-analytics-and-extensions.md
├── scripts/
│   ├── README.md
│   ├── 00-capability-scan.sql
│   ├── 01-cluster-inventory.sql
│   ├── 02-table-and-colocation-inventory.sql
│   ├── 03-shard-skew-and-placement.sql
│   ├── 04-partition-health.sql
│   ├── 05-query-and-connection-diagnostics.sql
│   ├── 06-topology-change-preflight.sql
│   ├── 07-safe-auth-audit.sql
│   ├── capacity_model.py
│   └── validate-package.py
├── assets/
│   ├── architecture-review-template.md
│   ├── design-decision-record-template.md
│   ├── migration-runbook-template.md
│   ├── performance-experiment-template.md
│   └── incident-report-template.md
└── tests/
    ├── skill-evaluation-prompts.md
    └── test_capacity_model.py
```

`SKILL.md` contains the behavior, decision order, safety rules, routing table, and output contract. Deep technical material lives in `references/` so Codex can load only what a task requires. The SQL under `scripts/` is intended for read-only evidence collection, while `capacity_model.py` performs deterministic planning arithmetic without connecting to a database. The files under `assets/` are reusable engineering templates.

## Installation

A Codex skill is a directory containing `SKILL.md`. Install the complete directory, not only the Markdown file.

### Install globally for every project

#### Linux or macOS

```bash
mkdir -p "$HOME/.agents/skills"
cp -R citus-skill "$HOME/.agents/skills/citus-engineering"
```

Or clone a published repository directly:

```bash
git clone https://github.com/int04/citus-skill.git \
  "$HOME/.agents/skills/citus-engineering"
```

#### Windows PowerShell

```powershell
New-Item -ItemType Directory -Force \
  "$env:USERPROFILE\.agents\skills" | Out-Null

Copy-Item -Recurse -Force \
  ".\citus-skill" \
  "$env:USERPROFILE\.agents\skills\citus-engineering"
```

Or clone it:

```powershell
git clone https://github.com/int04/citus-skill.git `
  "$env:USERPROFILE\.agents\skills\citus-engineering"
```

The final path must contain:

```text
$HOME/.agents/skills/citus-engineering/SKILL.md
```

On Windows, the equivalent is:

```text
C:\Users\<USER>\.agents\skills\citus-engineering\SKILL.md
```

### Install for one repository

Place the directory under the repository's `.agents/skills` folder:

```text
<REPOSITORY>/.agents/skills/citus-engineering/SKILL.md
```

## Invocation

Explicit invocation in Codex CLI or the IDE extension:

```text
$citus-engineering
```

Examples:

```text
$citus-engineering Review this schema and classify every table as local,
reference, distributed, schema-based, partitioned, or columnar. Score the
candidate distribution keys from the actual queries and produce a migration
plan with validation and rollback.
```

```text
$citus-engineering Design a Citus time-series model. Distribute by device_id,
partition by event_time, retain raw data for 180 days, keep the newest 14 days
updatable, and convert older immutable partitions to columnar storage. Include
relation-count and connection-budget calculations.
```

```text
$citus-engineering Explain why this query fans out to every shard. Classify its
query path, compare EXPLAIN plans, and propose the smallest measurable rewrite.
```

```text
$citus-engineering Write a safe runbook to add two workers, preview the
rebalance, monitor WAL and connections, verify placement balance, and define
abort and rollback conditions.
```

```text
$citus-engineering Investigate this Citus error. Preserve the SQLSTATE, identify
which node raised it, give read-only checks first, and rank root causes by
evidence.
```

## How the skill treats versions

The repository is not a promise that every function, argument, view, or GUC exists in every Citus release or managed service. The connected database is the source of truth. The skill requires capability detection through `citus_version()`, `pg_extension`, `pg_proc`, `pg_settings`, metadata views, and provider documentation before version-sensitive operations.

The documentation was reviewed against the official Citus 14 documentation family and PostgreSQL 18 documentation available on 2026-08-15, while deliberately retaining runtime compatibility checks for older, newer, and provider-modified environments. See `references/13-official-sources-and-version-policy.md`.

## Safety model

Commands are classified as `READ`, `SESSION`, `WRITE`, `IMPACT`, or `DESTRUCTIVE`.

For data movement, schema conversion, storage conversion, partition removal, or topology changes, the skill requires:

- confirmed scope and capability;
- a read-only preflight;
- capacity and lock analysis;
- a representative staging rehearsal;
- phase checkpoints;
- live metrics and abort conditions;
- validation commands and acceptance criteria;
- rollback or forward-recovery ownership;
- cleanup only after completion criteria are met.

The skill never treats rebalance or drain as an independent backup and never instructs Codex to print secrets from Citus authentication metadata.

## Read-only diagnostic scripts

Run a script with `psql` against the database in which Citus is installed:

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 \
  -f scripts/00-capability-scan.sql
```

The scripts intentionally avoid project-specific table names. Review query cost before running cluster-wide diagnostics on very large clusters; some health checks can open many connections or scan substantial metadata.

## Capacity-planning calculator

Use the deterministic calculator to expose shard, partition, relation, and upper-bound connection arithmetic before recommending a topology. It does not connect to PostgreSQL and does not turn heuristic inputs into a production recommendation.

```bash
python3 scripts/capacity_model.py \
  --workers 4 \
  --cores-per-worker 8 \
  --max-connections-per-worker 200 \
  --reserved-connections-per-worker 40 \
  --shard-count 64 \
  --active-leaf-partitions 12 \
  --indexes-per-leaf-partition 3 \
  --concurrent-multi-shard-queries 5 \
  --shards-touched-per-query 16 \
  --total-logical-data-gb 1024
```

The output estimates:

- shards per worker and worker core;
- usable worker connection budget;
- an intentionally conservative upper bound for coordinator-to-worker connections;
- physical leaf table and index placements from sharding plus partitioning;
- average logical shard and shard-partition sizes when data volume is supplied;
- arithmetic warnings that still require `EXPLAIN`, workload tests, and runtime capability checks.

Add `--json` for machine-readable output. Treat every result as a planning estimate, not as a universal shard-count or connection recommendation.

## Validation

Validate the package structure, internal links, frontmatter, English-only content, and read-only SQL policy:

```bash
python3 scripts/validate-package.py
python3 -m unittest discover -s tests -p 'test_*.py'
```

The same structural validation and calculator tests run automatically in GitHub Actions through `.github/workflows/validate.yml`.

The validation script does not prove that every SQL statement is supported by a particular Citus release. Runtime capability detection remains mandatory.

## Design philosophy

- Data model before tuning knobs.
- Locality before parallelism.
- Query and transaction evidence before distribution-key choice.
- Shard and partition counts as capacity decisions, not magic constants.
- Partitioning for pruning and lifecycle, not as a substitute for sharding.
- Measured experiments before permanent GUC changes.
- Metadata and validation before data movement.
- Recovery design before destructive execution.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). New guidance should cite official sources, identify version boundaries, preserve infrastructure neutrality, and include risk plus validation semantics. Pull requests are checked by the included GitHub Actions validation workflow.

## License

MIT. See [LICENSE](LICENSE).

## Disclaimer

This repository is an engineering aid, not a substitute for a production-specific architecture review, change-management process, security policy, backup/restore test, or support agreement. Always verify commands against the installed versions and rehearse high-impact changes on representative data.
