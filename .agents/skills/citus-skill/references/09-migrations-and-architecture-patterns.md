# Citus Migrations and Architecture Patterns

A Citus migration is a data-locality redesign, not only a database copy. The migration must preserve application behavior while changing table types, keys, constraints, query routing, and operational ownership.

## 1. Migration principles

1. Choose the distribution model from workload evidence before editing schema.
2. Inventory every table, query, transaction, dependency, and extension.
3. Propagate the distribution key through related tables deliberately.
4. Preserve a rollback boundary until target correctness is proven.
5. Separate schema conversion, data movement, application changes, and cutover.
6. Validate with independent counts/aggregates and application shadow traffic.
7. Rehearse on representative volume and skew.
8. Do not combine distribution redesign, partition redesign, storage conversion, and major version upgrade in one opaque step unless unavoidable.

## 2. Discovery inventory

Collect:

### Database

- PostgreSQL and Citus versions;
- extensions, custom types, functions, triggers, sequences, views, materialized views;
- schemas, tables, partitions, indexes, constraints, and owners;
- table/partition sizes and growth;
- row counts by business key/time range;
- autovacuum/analyze and bloat;
- logical replication/CDC dependencies;
- backup and restore method.

### Application

- connection endpoints and pooling;
- ORM migrations and generated SQL;
- hot query fingerprints and parameters;
- transaction boundaries and lock behavior;
- background jobs, reports, exports, and admin scripts;
- retry/idempotency behavior;
- tenant/entity isolation assumptions;
- deployment and rollback mechanism.

### Workload

- read/write/ingest rates;
- latency and throughput objectives;
- concurrency and connection budget;
- query windows and retention;
- largest/hottest tenants/entities;
- cross-tenant/entity operations;
- future worker and data scale.

## 3. Table classification worksheet

Classify each table:

| Category | Meaning | Migration action |
|---|---|---|
| Ready for distribution | Already contains the chosen key | Review constraints, then distribute |
| Needs key backfill | Belongs to a tenant/entity but lacks direct key | Add, backfill, validate, make non-null |
| Reference | Small shared lookup/dimension | Replicate as reference table |
| Local | Small control/admin relation not in hot distributed joins | Keep local |
| Managed local | Local data must be available from metadata/query nodes | Add to Citus metadata when supported |
| Independent distributed | Large table with a different workload boundary | Separate colocation group or architecture |
| Schema-distributed | Tenant/service naturally isolated by schema | Evaluate schema-based sharding |
| Partition candidate | Requires pruning/retention/hot-cold lifecycle | Design PostgreSQL partitioning |
| Columnar candidate | Immutable analytical history | Validate limits, then convert selectively |
| Externalized | Better owned by another service/store | Migrate outside this Citus model |

Do not mark every large table “distributed” without identifying its routing key and hot queries.

## 4. Distribution-key migration

### Candidate scorecard

Score each candidate 0–5:

- frequency in hot `WHERE` predicates;
- frequency in join predicates;
- transaction locality;
- cardinality;
- evenness/skew;
- immutability;
- ability to backfill every related table;
- compatibility with foreign keys and uniqueness;
- ability to isolate/move a hot tenant/entity;
- future architecture fit.

Document disqualifiers. A high-cardinality key that is absent from queries is not good. A common key with one dominant value may be worse.

### Propagate the key

For a child table that reaches tenant/entity only through joins:

1. add nullable key column;
2. backfill in bounded batches;
3. validate against the owning table;
4. update application writes to populate it;
5. add not-null when complete;
6. update primary/unique/foreign keys;
7. add indexes;
8. distribute and colocate.

Backfill template:

```sql
UPDATE <SCHEMA>.<CHILD_TABLE> AS c
SET <DIST_COLUMN> = p.<DIST_COLUMN>
FROM <SCHEMA>.<PARENT_TABLE> AS p
WHERE c.parent_id = p.id
  AND c.<DIST_COLUMN> IS NULL
  AND <BOUNDED_BATCH_PREDICATE>;
```

On large tables, use restartable batches and monitor WAL, locks, vacuum, and replication lag.

### Validation

```sql
SELECT count(*) AS missing_key
FROM <SCHEMA>.<CHILD_TABLE>
WHERE <DIST_COLUMN> IS NULL;
```

```sql
SELECT count(*) AS mismatched_key
FROM <SCHEMA>.<CHILD_TABLE> AS c
JOIN <SCHEMA>.<PARENT_TABLE> AS p
  ON p.id = c.parent_id
WHERE c.<DIST_COLUMN> IS DISTINCT FROM p.<DIST_COLUMN>;
```

## 5. Constraint redesign

A distributed primary or unique constraint generally needs the distribution column. A partitioned parent can also require the partition key.

Example transformation:

```text
Before: PRIMARY KEY (order_id)
After:  PRIMARY KEY (tenant_id, order_id)
```

For a distributed and time-partitioned table:

```text
PRIMARY KEY (tenant_id, event_id, event_time)
```

But verify whether that matches business identity. Alternatives for global identity include:

- application-generated globally unique ID plus tenant-scoped database key;
- a centralized idempotency/lookup registry;
- composite references that carry the distribution key;
- schema-based sharding;
- retaining a table local/reference when scale permits.

Review foreign keys after distribution, not before assuming they propagate.

## 6. Query migration

Update queries to carry the distribution key through:

- point reads;
- updates/deletes;
- joins;
- pagination;
- background jobs;
- authorization filters;
- cache keys;
- event payloads and APIs.

Before:

```sql
SELECT *
FROM orders
WHERE order_id = $1;
```

After:

```sql
SELECT *
FROM orders
WHERE tenant_id = $1
  AND order_id = $2;
```

This is not only a performance change. It makes tenant ownership explicit and can improve authorization safety.

Keep a list of unavoidable cross-tenant operations and classify each as:

- rare admin operation;
- asynchronous rollup/report;
- separate analytics path;
- reference-table join;
- architecture mismatch requiring redesign.

## 7. Greenfield row-based pattern

Good for high-density SaaS, conversations, accounts, devices, or entities.

```text
tenants/accounts                 distributed by tenant_id
orders/messages/events          distributed by tenant_id, colocated
child rows                      distributed by tenant_id, colocated
small shared lookups            reference
control/config/admin            local or managed local
large unrelated analytics       independent colocation group
```

Rules:

- every tenant transaction includes `tenant_id`;
- tables in the same transaction group share key type and colocation;
- unrelated tables use `colocate_with => 'none'`;
- global reporting is asynchronous or benchmarked as multi-shard;
- hot tenants can be isolated when necessary.

## 8. Greenfield schema-based pattern

Good for tenant/service-per-schema systems with minimal row-key changes.

```text
tenant_a.*   one distributed schema/colocation group
tenant_b.*   another distributed schema/colocation group
shared.*     local/reference/shared design based on workload
```

Evaluate:

- schema count and churn;
- largest schema size;
- cross-schema joins and foreign keys;
- DDL propagation and migration tooling;
- `search_path` safety;
- placement and move granularity;
- tenant onboarding/offboarding;
- reporting across schemas.

Do not use schema-based sharding merely to avoid thoughtful modeling when tenant density is very high.

## 9. Time-series pattern

```text
Citus distribution: tenant_id/device_id/repository_id
PostgreSQL partition: event_time range
Hot storage: heap
Cold storage: columnar after freeze
Retention: detach/archive/drop by partition
Rollup: colocated by same distribution key and time bucket
```

Required calculations:

- ingest bytes per interval;
- retained partition count;
- shard × partition × index relation count;
- connection budget for fan-out queries;
- late-arrival distribution;
- hot-window size;
- conversion, retention, and restore duration.

See `03-partitioning-and-time-series.md`.

## 10. Real-time analytics pattern

Goal: parallelize scans and partial aggregates across workers.

Choose an entity distribution key that is:

- high-cardinality;
- even;
- common in joins/grouping;
- not dominated by one value.

Use reference tables for small dimensions. Preaggregate frequently repeated global queries. Benchmark shard count relative to worker cores and connection budget.

Avoid:

- low-cardinality status distribution;
- one enormous entity without isolation strategy;
- arbitrary cross-shard joins in latency-critical dashboards;
- pulling raw data to the coordinator for final processing.

## 11. Mixed OLTP and analytics pattern

Options:

1. same distributed tables with workload-specific pools and limits;
2. colocated rollup tables;
3. historical columnar partitions;
4. asynchronous replica/secondary analytics system;
5. separate Citus colocation groups or database boundaries.

Protect OLTP from analytics by:

- routing keys and indexes;
- connection/session limits;
- executor pool settings;
- statement timeouts and resource queues at the application layer;
- scheduling heavy work;
- materialized/rollup data;
- monitoring coordinator and worker saturation.

## 12. Social/conversation pattern

Potential distribution domains:

- `conversation_id` when messages/participants/actions are conversation-local;
- `user_id` when personal inbox/profile operations dominate;
- `community_id` or `workspace_id` for group platforms.

No single key may optimize every graph query. Choose the primary transactional domain and create derived/read models for secondary access patterns.

Example:

```text
messages, receipts, attachments   by conversation_id
user inbox summary                derived table by user_id
friend/follower graph             separate workload/model
shared media metadata             reference/local/external by size and access
```

Do not distribute each related table by its own primary key and expect efficient joins.

## 13. Offline migration phases

### Phase 0 — design approval

- workload and key scorecard;
- table classification;
- target constraints and colocation;
- shard/partition/capacity plan;
- application query changes;
- backup and rollback plan.

### Phase 1 — target schema

- create extensions/types/functions;
- create tables, partitioned parents, and required partitions;
- distribute/reference/manage tables in dependency order;
- add constraints and indexes;
- verify metadata.

### Phase 2 — load

- stop source writes;
- load in bounded/restartable units;
- analyze;
- validate counts/aggregates;
- test application queries.

### Phase 3 — cutover

- switch connection endpoint/configuration;
- deploy query changes;
- run smoke tests;
- monitor errors, latency, connections, WAL, and locks.

### Phase 4 — rollback boundary

Keep source recoverable until acceptance criteria and business observation window pass.

## 14. Online migration phases

### Phase 1 — compatible schema change

Add distribution columns and application support without removing old behavior.

### Phase 2 — target build

Create distributed/partitioned target and backfill history.

### Phase 3 — change synchronization

Use logical replication, CDC, dual write, or event replay. Each has consistency and failure trade-offs.

### Phase 4 — shadow validation

Compare reads and aggregates by tenant/time range. Measure lag and error rate.

### Phase 5 — write cutover

Move writes to target while preserving idempotency and rollback.

### Phase 6 — read cutover

Shift reads gradually or by tenant/cohort.

### Phase 7 — decommission

Only after the rollback window, backup, and reconciliation close.

## 15. Dual-write warning

Application dual writes are difficult to make atomically across databases. Define:

- order of writes;
- retry and idempotency keys;
- outbox/inbox or event log;
- reconciliation job;
- failure handling when one side succeeds;
- cutover source of truth.

Prefer a transactional outbox/CDC design over naïve independent writes when correctness matters.

## 16. Shadow-table cutover

A common same-database pattern:

1. create `<TABLE>_new` with target design;
2. backfill;
3. mirror ongoing writes through controlled logic;
4. validate;
5. acquire a bounded cutover lock;
6. apply final delta;
7. rename/swap views or application references;
8. retain old table read-only through rollback window.

Review foreign keys, sequences, privileges, views, triggers, publication membership, and ORM metadata during rename.

## 17. Change shard count or distribution key migration

`alter_distributed_table()` may support these changes, but they move data and can cascade through colocated tables.

Use in-place change when:

- version supports the exact arguments;
- data movement fits the window;
- replica identity and capacity are ready;
- rollback/forward recovery is clear.

Use a shadow-table migration when:

- the change is large or high-risk;
- the old and new schemas differ materially;
- online synchronization is needed;
- a clean rollback boundary is more important than simplicity.

## 18. Partition introduction migration

For a large unpartitioned distributed table:

- do not assume it can become partitioned with one cheap command;
- build a partitioned shadow parent;
- distribute with compatible colocation;
- create partitions;
- copy period by period;
- validate and cut over;
- archive/drop old table only after acceptance.

A table rewrite plus shard movement plus new partitions can multiply resource demand. Separate phases where possible.

## 19. Columnar introduction migration

Convert only closed data:

1. define freeze criteria;
2. identify eligible partitions;
3. test required SQL and index/constraint loss;
4. convert one bounded partition;
5. compare compression and query performance;
6. expand gradually;
7. retain reverse conversion procedure.

## 20. Validation framework

Use several dimensions:

### Structural

- table types, distribution columns, colocation IDs, shard counts;
- partition bounds and access methods;
- indexes and constraints;
- roles, ownership, extensions, functions.

### Data

- counts by tenant/time range;
- sums/min/max/checksums;
- duplicate and null checks;
- orphan/foreign-key checks;
- source/target sample comparison.

### Behavioral

- application smoke/integration tests;
- query plans and routing;
- transaction and lock behavior;
- retries and idempotency;
- background jobs and exports.

### Performance

- throughput and p50/p95/p99;
- CPU/disk/network per node;
- connections and fan-out;
- planning time;
- shard/tenant/partition skew.

### Recovery

- rollback procedure;
- backup restore;
- failover during migration;
- final delta replay;
- ownership and timing.

## 21. Migration acceptance criteria example

```text
- 100% of target tables have approved table type and owner.
- Zero NULL or mismatched distribution keys.
- Source/target counts match for every tenant/day in the migration window.
- Financial/metric aggregates match within explicitly approved tolerance.
- Top 20 queries route as designed and meet p95 thresholds.
- No worker exceeds disk/connection headroom thresholds.
- Backup and rollback were tested.
- Application error rate remains below threshold for the observation window.
```

## 22. Migration anti-patterns

- Distribute every table in one transaction.
- Choose the key from column names instead of query/transaction evidence.
- Backfill an unbounded table with one massive update.
- Change primary keys without updating every foreign reference and application cache key.
- Run data movement while peak traffic, backup, index build, and rebalance overlap.
- Use approximate catalog row counts as final correctness proof.
- Cut over writes without idempotency and reconciliation.
- Remove the source immediately after the first successful query.
- Combine a PostgreSQL major upgrade, Citus major upgrade, distribution-key change, partition introduction, and columnar conversion into one unrehearsed event.

## 23. Architecture/migration review checklist

- [ ] Measured requirement for Citus is documented.
- [ ] Row-based, schema-based, and no-sharding alternatives were compared.
- [ ] Every table is classified.
- [ ] Distribution-key scorecard uses real queries and transactions.
- [ ] Colocation groups match business transaction domains.
- [ ] Constraint and identifier strategy is complete.
- [ ] Shard, partition, relation, connection, and storage capacity are projected.
- [ ] Application queries carry the routing key.
- [ ] Cross-shard operations are listed and justified.
- [ ] Migration phases have independent checkpoints.
- [ ] Backfill is bounded, restartable, and observable.
- [ ] Online synchronization has idempotency and reconciliation.
- [ ] Source/target validation is independent.
- [ ] Rollback remains available through an observation window.
- [ ] Backup, restore, and failure during migration were tested.
