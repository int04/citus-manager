# Citus DML, Transactions, and Ingestion

Design writes around the distribution key and transaction boundary. The fastest, simplest write is one that identifies a single shard and keeps all related rows colocated.

## 1. Write-path classification

Classify each write before optimizing it:

| Path | Description | Main concern |
|---|---|---|
| Single-row, single-shard | Distribution key is known and one shard is touched | Local index/lock cost |
| Multi-row, one shard | Batch contains one distribution-key value | Batch size and local transaction cost |
| Multi-row, many shards | Batch routes to multiple workers | Connection fan-out and commit protocol |
| `INSERT ... SELECT` colocated | Source and target align on distribution key | Pushdown and key preservation |
| `INSERT ... SELECT` repartitioned | Rows must move to target shards | Network and intermediate data |
| Multi-shard update/delete | Predicate touches several shards | Distributed locking and atomicity |
| Bulk initial load | Large historical dataset | Staging, parallelism, validation, WAL |
| Continuous high-rate ingest | Long-lived connections and recurring batches | Throughput, backpressure, partition readiness |

## 2. INSERT requirements

A distributed insert must provide a routable distribution value unless the table model and installed version provide another valid route.

```sql
INSERT INTO <SCHEMA>.<TABLE> (
  <DIST_COLUMN>, id, created_at, payload
)
VALUES (
  <DIST_VALUE>, <ID_VALUE>, now(), <PAYLOAD>
);
```

Validate:

- the distribution column is non-null where required;
- the value is stable and belongs to the intended tenant/entity;
- primary/unique keys include the distribution column when required;
- a matching PostgreSQL partition exists;
- reference-table values and foreign keys are present;
- retries cannot create duplicates.

## 3. Multi-row INSERT

Batch rows to reduce round trips:

```sql
INSERT INTO <SCHEMA>.<TABLE> (
  <DIST_COLUMN>, id, created_at, payload
)
VALUES
  (<DIST_1>, <ID_1>, <TS_1>, <PAYLOAD_1>),
  (<DIST_2>, <ID_2>, <TS_2>, <PAYLOAD_2>),
  (<DIST_3>, <ID_3>, <TS_3>, <PAYLOAD_3>);
```

Trade-offs:

- larger batches reduce client overhead;
- multi-shard batches can open more worker connections;
- one failed row can abort the statement;
- enormous statements increase memory, parsing, WAL, lock duration, and retry cost;
- grouping rows by distribution value can improve locality and reduce churn.

Benchmark batch size under real latency and concurrency. Do not optimize only for one isolated connection.

## 4. COPY

Use `COPY` or `\copy` for bulk ingestion when compatible with the source and operational constraints.

```sql
COPY <SCHEMA>.<TABLE> (
  <DIST_COLUMN>, id, created_at, payload
)
FROM STDIN WITH (FORMAT csv);
```

Operational design:

- include the distribution column in every row;
- pre-create required time partitions;
- prefer long-lived database connections;
- size batches/files so retries are bounded;
- monitor worker connections, CPU, WAL, disk, and network;
- avoid loading through an application endpoint designed for single-row transactions;
- validate row counts, rejected rows, min/max keys, and aggregates per tenant/time range;
- analyze after a large load;
- account for indexes and foreign keys during initial load.

`COPY` can touch many shards. It does not make connection and transaction capacity irrelevant.

## 5. Initial-load strategies

### Strategy A — load directly into the final distributed table

Use when:

- schema and distribution design are stable;
- data is clean and already contains the distribution key;
- indexes/constraints will not make load time unacceptable;
- rollback can discard or recreate the target.

### Strategy B — staging table then transform

Use when:

- source data needs cleaning, deduplication, or key backfill;
- target partition/constraint routing must be validated;
- transformations should be tested separately;
- incremental cutover is required.

The staging table can be local or distributed depending on size and transformation path. A large local staging table can overload the coordinator.

### Strategy C — partition-by-partition historical load

Use for time-series data:

1. create a bounded partition;
2. load and validate it;
3. analyze it;
4. optionally convert it to columnar after immutability is proven;
5. continue with the next range.

This creates clear checkpoints and limits retry scope.

### Strategy D — shadow cluster/table with change capture

Use for large online migrations. Build the target, bulk load history, stream changes, validate lag and consistency, then cut over. See `09-migrations-and-architecture-patterns.md`.

## 6. INSERT ... SELECT

Citus can execute `INSERT ... SELECT` through different paths depending on distribution compatibility and query shape.

Preferred form preserves the target distribution key:

```sql
INSERT INTO <SCHEMA>.<TARGET_TABLE> (
  <DIST_COLUMN>, bucket_start, metric_value
)
SELECT <DIST_COLUMN>,
       date_trunc('hour', event_time) AS bucket_start,
       count(*)
FROM <SCHEMA>.<SOURCE_TABLE>
WHERE event_time >= <START_BOUND>
  AND event_time <  <END_BOUND>
GROUP BY <DIST_COLUMN>, date_trunc('hour', event_time);
```

Before execution:

```sql
EXPLAIN (VERBOSE, COSTS ON)
INSERT INTO <SCHEMA>.<TARGET_TABLE> (...)
SELECT ...;
```

Check whether the plan:

- pushes work to workers;
- preserves colocation;
- repartitions rows;
- materializes intermediate results;
- pulls data through the coordinator;
- touches the expected shards and partitions.

Do not assume matching column names imply matching distribution semantics.

## 7. Upsert and idempotency

Use `ON CONFLICT` only when the target constraint is valid for the distributed and partitioned design.

```sql
INSERT INTO <SCHEMA>.<TABLE> (
  <DIST_COLUMN>, external_id, event_time, payload
)
VALUES (
  <DIST_VALUE>, <EXTERNAL_ID>, <EVENT_TIME>, <PAYLOAD>
)
ON CONFLICT (<DIST_COLUMN>, external_id, event_time)
DO UPDATE
SET payload = EXCLUDED.payload;
```

Questions:

- Is the conflict key enforceable across shards?
- Does a partitioned parent also require the partition key in the unique constraint?
- Are retries safe if the same event arrives in a different partition?
- Should immutable events be `DO NOTHING` instead of updated?
- Does the update target a columnar partition that cannot be modified?
- Can concurrent retries produce a lost update?

For globally unique external IDs that do not contain the distribution key, consider:

- embedding tenant/entity into the identifier;
- a colocated idempotency key;
- a small centralized uniqueness registry with known throughput limits;
- application-level ownership and reconciliation;
- a different distribution model.

## 8. UPDATE

Preferred update includes the distribution key and stable primary key:

```sql
UPDATE <SCHEMA>.<TABLE>
SET status = <NEW_STATUS>,
    updated_at = now()
WHERE <DIST_COLUMN> = <DIST_VALUE>
  AND id = <ID_VALUE>;
```

Avoid treating the distribution column as an ordinary mutable attribute. Changing it can require moving a row between shards and can break colocated relationships.

If business ownership changes:

1. define all affected tables;
2. copy data to the new distribution value in dependency order;
3. validate constraints and counts;
4. switch references/application state;
5. delete the old copy only after verification;
6. preserve an audit trail and rollback boundary.

Use a dedicated migration, not an ad hoc `UPDATE <DIST_COLUMN>`.

## 9. DELETE and retention

Single-shard delete:

```sql
DELETE FROM <SCHEMA>.<TABLE>
WHERE <DIST_COLUMN> = <DIST_VALUE>
  AND id = <ID_VALUE>;
```

Large time-based retention should usually drop or detach partitions rather than delete millions of rows and vacuum the resulting dead tuples.

For nonpartitioned bulk deletes:

- bound each batch;
- include the distribution key when possible;
- monitor WAL, replication lag, locks, autovacuum, and disk;
- preserve restartability;
- validate each range before continuing.

Deletion of a partition or tenant is `DESTRUCTIVE`. Retention policy and restore behavior must be explicit.

## 10. Transaction locality

### Single-shard transaction

Preferred for OLTP:

```sql
BEGIN;

INSERT INTO <SCHEMA>.orders (..., <DIST_COLUMN>)
VALUES (..., <DIST_VALUE>);

INSERT INTO <SCHEMA>.order_items (..., <DIST_COLUMN>)
VALUES (..., <DIST_VALUE>);

COMMIT;
```

Requirements:

- same distribution value;
- compatible colocation;
- constraints and joins align with that value;
- application carries the key through every statement.

### Multi-shard transaction

Valid when necessary, but evaluate:

- atomicity requirements;
- two-phase commit behavior and configuration;
- prepared-transaction capacity;
- worker failure/recovery semantics;
- lock ordering and distributed deadlock risk;
- connection fan-out;
- retry behavior after uncertain client outcomes.

Do not enable or force a distributed commit protocol merely to hide a poor transaction boundary.

## 11. Commit protocol

Inspect installed settings:

```sql
SELECT name, setting, context, source
FROM pg_settings
WHERE name IN (
  'citus.multi_shard_commit_protocol',
  'max_prepared_transactions'
)
ORDER BY name;
```

Single-shard writes normally do not need distributed two-phase commit. Multi-shard atomic operations may require a stronger protocol depending on version and semantics.

Before using 2PC:

- ensure `max_prepared_transactions` is configured appropriately on relevant nodes;
- monitor `pg_prepared_xacts`;
- understand recovery of in-doubt transactions;
- test worker/coordinator failure during prepare and commit;
- define incident ownership for stranded prepared transactions.

Never manually commit or roll back an unknown prepared transaction without proving its global outcome.

## 12. Distributed deadlocks

Cross-node transactions can deadlock even when each local lock view appears incomplete.

Prevention:

- keep transactions single-shard where possible;
- update tables and keys in a consistent global order;
- avoid interactive/long transactions;
- reduce batch size;
- lock the smallest necessary set;
- preserve exact SQLSTATE and node logs during incidents.

Do not “solve” deadlocks by blindly increasing lock timeouts.

## 13. SELECT FOR UPDATE and row locking

Treat row-locking support as query-path and version sensitive. Single-shard routed forms are the safest expectation:

```sql
SELECT *
FROM <SCHEMA>.<TABLE>
WHERE <DIST_COLUMN> = <DIST_VALUE>
  AND id = <ID_VALUE>
FOR UPDATE;
```

Cross-shard `FOR UPDATE`, recursive CTEs, outer joins, and correlated subqueries can have restrictions. Check current SQL support and test the exact query.

## 14. Distributed functions

A distributed function can route tenant/entity-scoped procedural logic to the correct worker when the installed version supports it.

Conceptual pattern:

```sql
CREATE OR REPLACE FUNCTION <SCHEMA>.process_entity(
  p_<DIST_COLUMN> bigint,
  p_id bigint
)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
  -- Multiple statements that all remain within p_<DIST_COLUMN>.
END;
$$;

SELECT create_distributed_function(
  '<SCHEMA>.process_entity(bigint,bigint)',
  distribution_arg_name => 'p_<DIST_COLUMN>',
  colocate_with => '<SCHEMA>.<ROOT_TABLE>'
);
```

Use when:

- the function has a clear routing argument;
- all touched distributed tables are colocated;
- function dependencies exist on workers;
- volatility, permissions, search path, and error behavior are understood.

Avoid when:

- the function hides cross-tenant work;
- dynamic SQL can target arbitrary shards;
- schema qualification is missing;
- deployments cannot propagate function changes safely.

## 15. Rollups and aggregate tables

Rollups reduce repeated multi-shard scans.

Design:

- distribute rollup and source by the same key when tenant/entity queries dominate;
- include bucket boundary and dimensions in the key;
- make aggregation idempotent;
- define late-arrival correction;
- validate raw completeness before retention deletes;
- benchmark whether rollup creation is pushed down or coordinator-heavy.

Example key:

```sql
PRIMARY KEY (<DIST_COLUMN>, bucket_start, metric_name)
```

Incremental upsert:

```sql
INSERT INTO <SCHEMA>.hourly_metrics (...)
SELECT ...
ON CONFLICT (<DIST_COLUMN>, bucket_start, metric_name)
DO UPDATE SET metric_value = EXCLUDED.metric_value;
```

Do not increment counters blindly under retries unless duplicate processing is impossible or compensated.

## 16. Identifier generation

Options:

- application-generated UUID/ULID-style identifiers;
- composite key containing distribution value;
- BIGINT sequences with Citus behavior verified;
- per-tenant/entity sequence stored and updated on the owning shard;
- externally assigned immutable IDs.

Review:

- uniqueness scope;
- index locality and width;
- insert order and page splits;
- retry/idempotency needs;
- ability to route by the identifier alone;
- behavior when inserts occur from different nodes.

A globally unique ID does not automatically make it a good distribution key.

## 17. Backpressure and overload control

High-rate ingestion must protect the cluster:

- cap application pool size;
- bound batch size and in-flight requests;
- separate ingestion and analytical connection pools;
- monitor worker connection queues and Citus shared pools;
- reject or queue traffic before WAL/disk exhaustion;
- keep future partitions ready;
- avoid running rebalance, index builds, retention, and peak ingestion concurrently without capacity evidence.

## 18. Validation for a write migration

Use multiple independent checks:

- source and target row counts by tenant/time range;
- min/max IDs and timestamps;
- grouped counts and sums;
- null and orphan checks;
- duplicate/idempotency-key checks;
- constraint validation;
- sample record comparison;
- application shadow reads;
- query-plan and latency comparison;
- WAL/replication lag and error rates.

Exact `count(*)` can be expensive. Use bounded ranges and staged validation, but do not rely only on approximate catalog counts for final correctness.

## 19. DML anti-patterns

- Row-by-row inserts over high-latency connections.
- Missing distribution key in hot write predicates.
- One huge multi-shard transaction for a batch job.
- Updating the distribution column as normal business logic.
- Upsert against a key that cannot be enforced across shards/partitions.
- Loading into a time-partitioned table without future partitions.
- Copying all data through a coordinator-local staging table that cannot fit.
- Using columnar history while normal updates/deletes still target it.
- Retrying uncertain multi-shard commits without idempotency.
- Manually resolving prepared transactions without global evidence.

## 20. DML review checklist

- [ ] Every write has a known distribution value or an explicitly justified multi-shard path.
- [ ] Batch size is benchmarked under peak concurrency.
- [ ] Required partitions exist before ingest.
- [ ] Upsert constraints are enforceable in both Citus and PostgreSQL partitioning.
- [ ] Transaction boundaries match colocation groups.
- [ ] Multi-shard atomicity and commit protocol are intentional.
- [ ] Retry and idempotency behavior is documented.
- [ ] Initial-load validation is independent and restartable.
- [ ] Late-arrival behavior is compatible with hot/cold storage.
- [ ] Backpressure protects connections, WAL, disk, and workers.
- [ ] Rollups are idempotent and preserve locality.
- [ ] Destructive cleanup happens only after target validation.
