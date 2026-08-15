# Citus Columnar and Hybrid Storage

Columnar storage is a workload-specific access method, not a default replacement for PostgreSQL heap tables. Use it for immutable or append-dominant analytical data where compression and column projection outweigh update, delete, index, and transactional limitations.

Always inspect the installed Citus version and current columnar documentation. Capability and limitation details can change between releases.

## 1. Decision matrix

| Requirement | Heap | Columnar | Hybrid partitions |
|---|---|---|---|
| Frequent point updates/deletes | Strong fit | Usually poor or unsupported | Keep mutable window in heap |
| Selective indexed lookup | Strong fit | Limited compared with heap | Query hot heap; scan cold columnar |
| Large scans/aggregations | Good with enough I/O/CPU | Strong fit | Strong fit across history |
| Compression | Standard PostgreSQL behavior | Primary advantage | Compress only closed history |
| Foreign keys/unique constraints | Broad support | Version-sensitive/limited | Enforce where supported on heap/model |
| Logical decoding | Normal PostgreSQL rules | May be unsupported | Keep replication requirements in mind |
| Retention by time | Good with partitioning | Good for immutable partitions | Best common pattern |
| Late mutations | Straightforward | Problematic | Delay conversion until data freezes |

## 2. Good columnar candidates

- event/log/clickstream history after its mutation window closes;
- analytical fact tables loaded in batches;
- rollup tables rebuilt or appended by period;
- archived business records read mostly through scans and aggregates;
- wide tables where queries read a small subset of columns;
- cold time partitions retained for compliance or historical analysis.

Poor candidates:

- order/payment/workflow tables with status changes;
- queues and lock-based work claiming;
- tables requiring many selective B-tree lookups;
- tables with frequent delete-by-ID;
- small relations where compression does not justify complexity;
- data used by logical decoding or change-data-capture without verified support;
- relations requiring unsupported constraints or indexes.

## 3. Capability scan

```sql
SELECT citus_version();

SELECT n.nspname,
       p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname IN (
  'alter_table_set_access_method',
  'alter_columnar_table_set',
  'alter_old_partitions_set_access_method'
)
ORDER BY p.proname, arguments;

SELECT amname, amtype
FROM pg_am
WHERE amname IN ('heap', 'columnar');
```

Inspect table access methods:

```sql
SELECT n.nspname AS schema_name,
       c.relname AS relation_name,
       c.relkind,
       am.amname AS access_method
FROM pg_class AS c
JOIN pg_namespace AS n ON n.oid = c.relnamespace
LEFT JOIN pg_am AS am ON am.oid = c.relam
WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
ORDER BY n.nspname, c.relname;
```

## 4. Create a columnar table

When supported:

```sql
CREATE TABLE <SCHEMA>.<TABLE> (
  <DIST_COLUMN> bigint NOT NULL,
  event_time timestamptz NOT NULL,
  metric_name text NOT NULL,
  metric_value numeric
) USING columnar;
```

Then distribute it only if the data model requires worker scale:

```sql
SELECT create_distributed_table(
  '<SCHEMA>.<TABLE>',
  '<DIST_COLUMN>',
  colocate_with => 'none',
  shard_count => <SHARD_COUNT>
);
```

Verify whether creating columnar first and then distributing, or changing the access method after distribution, is the supported order for the installed release and table type.

## 5. Convert access method

Capability-gated template:

```sql
SELECT alter_table_set_access_method(
  '<SCHEMA>.<TABLE>',
  'columnar'
);
```

Convert back:

```sql
SELECT alter_table_set_access_method(
  '<SCHEMA>.<TABLE>',
  'heap'
);
```

Treat conversion as `IMPACT` because it rewrites data and can change supported indexes/constraints. Current official guidance notes that conversion to columnar drops indexes; verify this behavior in the installed version and include index recreation in any reverse plan.

Preflight:

- table and shard size;
- available disk during rewrite;
- WAL and replication impact;
- lock duration;
- active writes and long transactions;
- indexes and constraints that will be lost or invalid;
- downstream logical decoding/CDC;
- backup and restore path;
- query plans before and after.

## 6. Columnar stripe behavior

Columnar data is written in stripes and chunks. Current Citus guidance describes a stripe as one transaction's worth of rows up to an implementation limit. Consequences:

- tiny autocommit inserts can create many small stripes and poor compression;
- bulk inserts or COPY can improve compression and scan efficiency;
- one enormous transaction increases retry, WAL, memory, and lock cost;
- benchmark a bounded bulk size rather than maximizing transaction size blindly.

Inspect compression with:

```sql
VACUUM VERBOSE <SCHEMA>.<TABLE>;
```

Capture notices showing file size, row count, stripe count, and compression. Do not parse human-readable notices as a permanent monitoring API without version checks.

## 7. Current limitation categories

Treat these as capability checks, not eternal facts. Columnar releases have historically limited or omitted:

- `UPDATE` and `DELETE`;
- space reclamation for deleted/obsolete rows;
- B-tree and other index use comparable to heap;
- unique, primary-key, foreign-key, and exclusion constraints;
- tuple locks such as `SELECT ... FOR UPDATE`;
- serializable isolation behavior;
- logical decoding;
- certain trigger, replica identity, and replication workflows.

Before conversion, test the exact DDL and DML needed by the application. A successful `SELECT` benchmark does not prove operational compatibility.

## 8. Hybrid time-partitioned design

Recommended pattern:

```text
partitioned parent
├── newest partitions: heap + operational indexes
├── closed but mutable partitions: heap, reduced write rate
├── immutable historical partitions: columnar
└── expired partitions: detached/archive or dropped
```

Example parent:

```sql
CREATE TABLE <SCHEMA>.events (
  tenant_id bigint NOT NULL,
  event_id bigint NOT NULL,
  event_time timestamptz NOT NULL,
  payload jsonb NOT NULL,
  PRIMARY KEY (tenant_id, event_id, event_time)
) PARTITION BY RANGE (event_time);

SELECT create_distributed_table(
  '<SCHEMA>.events',
  'tenant_id',
  colocate_with => 'none',
  shard_count => <SHARD_COUNT>
);
```

Convert closed partitions individually:

```sql
SELECT alter_table_set_access_method(
  '<SCHEMA>.<CLOSED_PARTITION>',
  'columnar'
);
```

Or use a time helper when available:

```sql
CALL alter_old_partitions_set_access_method(
  '<SCHEMA>.events',
  now() - <HOT_WINDOW>,
  'columnar'
);
```

## 9. Freeze criteria

Do not convert based only on age. A partition is ready when all required conditions hold:

- normal updates/deletes have stopped;
- late-arriving event percentile is below the boundary;
- reconciliation/backfill is complete;
- CDC/logical-decoding requirements are finished or supported;
- constraints and indexes no longer need heap behavior;
- business/legal correction process is defined;
- rollback/re-hydration is tested.

Example policy:

```text
Convert a daily partition only when:
- it ended at least 30 days ago;
- no writes occurred for 7 consecutive days;
- reconciliation status is complete;
- row-count and aggregate checks passed;
- the partition has an independent recoverable backup tier.
```

## 10. Late corrections

Choose one policy:

1. keep a larger heap window;
2. write corrections to a separate heap adjustment table and combine at query time;
3. rebuild the historical partition from source;
4. convert columnar back to heap, apply corrections, validate, and reconvert;
5. reject corrections outside a defined window.

Document consistency semantics. An adjustment table can make queries more complex and should be colocated/distributed deliberately.

## 11. Query design for mixed storage

Queries against the parent can span heap and columnar partitions. Verify:

- partition pruning excludes irrelevant periods;
- hot point lookups stay in heap partitions;
- broad historical scans use column projection and worker aggregation;
- no query attempts mutation or row locking on columnar partitions;
- type, collation, and schema remain consistent across partitions;
- coordinator intermediate results are bounded.

Example:

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
SELECT tenant_id,
       date_trunc('day', event_time) AS day,
       count(*)
FROM <SCHEMA>.events
WHERE tenant_id = <TENANT_VALUE>
  AND event_time >= <START_BOUND>
  AND event_time <  <END_BOUND>
GROUP BY tenant_id, date_trunc('day', event_time);
```

Compare plans for a hot-only range, cold-only range, and mixed range.

## 12. Compression versus query trade-off

Measure:

- compression ratio;
- bytes read;
- CPU time for decompression;
- p50/p95/p99 latency;
- rows and columns scanned;
- worker balance;
- coordinator merge cost;
- storage and backup size;
- conversion time and WAL volume.

Columnar can reduce I/O but increase CPU. It is not automatically faster for selective or small-result queries.

## 13. Columnar options

When `alter_columnar_table_set()` exists, inspect its signature and official documentation before tuning stripe/chunk/compression parameters.

Do not copy option values from another dataset. Benchmark with:

- representative row width;
- common projected columns;
- sort/correlation patterns;
- batch transaction sizes;
- expected compression codec availability;
- worker CPU and storage limits.

Record the default and changed value in a performance experiment template.

## 14. Backup, restore, and upgrade

Test:

- physical backup of columnar relations;
- PITR through conversion operations;
- restore to the same extension version;
- extension upgrade compatibility;
- conversion rollback after a failed application release;
- managed-service support for the access method.

Do not assume a logical dump/restore path preserves every access-method option without verification.

## 15. Conversion runbook

### Phase 1 — evidence

```sql
SELECT table_name,
       citus_table_type,
       table_size,
       shard_count,
       access_method
FROM citus_tables
WHERE table_name = '<SCHEMA>.<TABLE>'::regclass;
```

Also collect indexes, constraints, dependencies, workload, writes, and partition bounds.

### Phase 2 — rehearsal

- restore representative data to staging;
- measure conversion duration, locks, disk, WAL, and query impact;
- test all required DML and DDL;
- verify reverse conversion and index recreation;
- run application and reporting tests.

### Phase 3 — production conversion

- stop or bound incompatible writes;
- confirm backup and monitoring;
- convert one partition/table at a time;
- validate immediately;
- abort on lock, latency, disk, WAL, or error thresholds.

### Phase 4 — validation

- row counts and aggregates match;
- access method is correct on all intended relations;
- expected indexes/constraints remain or are intentionally absent;
- hot and cold query latency meets thresholds;
- no application errors or unsupported mutations occur;
- backup/replication systems remain healthy.

## 16. Anti-patterns

- Convert an active OLTP table to columnar because it is large.
- Expect columnar to solve a poor distribution key or fan-out query.
- Insert one row per transaction into a high-volume columnar table.
- Convert partitions based only on calendar age while late writes continue.
- Ignore indexes that are dropped during conversion.
- Assume compression ratio alone proves success.
- Keep columnar history with no tested correction or restore path.
- Use columnar in a CDC/logical-decoding pipeline without capability proof.
- Convert all shards/partitions at once with no checkpoint.

## 17. Review checklist

- [ ] Workload is scan/aggregate or immutable enough for columnar.
- [ ] Installed capabilities and limitations were tested.
- [ ] Required indexes and constraints were inventoried.
- [ ] Batch size and stripe quality were benchmarked.
- [ ] Mutable hot data remains heap when needed.
- [ ] Freeze criteria include late-arrival and reconciliation evidence.
- [ ] Mixed heap/columnar query plans were tested.
- [ ] Conversion disk, WAL, locks, and duration fit the window.
- [ ] Reverse conversion and index recreation were rehearsed.
- [ ] Backup, restore, upgrade, and CDC behavior were verified.
