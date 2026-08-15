# PostgreSQL Partitioning and Time-Series Design with Citus

Use PostgreSQL partitioning and Citus sharding as separate dimensions:

- **Citus sharding** decides which worker owns a tenant/entity subset.
- **PostgreSQL partitioning** divides each logical table by time or another domain for pruning and lifecycle management.

The most common scalable time-series pattern is:

```text
hash-distribute by tenant/entity
          ×
range-partition by event time
```

This design can provide both shard pruning and partition pruning when queries contain both keys.

## 1. Partition only for a measurable reason

Good reasons:

- most queries touch a narrow recent window;
- old data is regularly expired, detached, archived, or converted;
- index and vacuum work needs smaller lifecycle units;
- hot and cold data require different access methods or storage policies;
- operational maintenance needs bounded chunks;
- retention must be implemented without large row-by-row deletes.

Weak reasons:

- the table is merely “large”;
- an index is missing;
- the distribution key was chosen poorly;
- all queries scan the full retention period;
- partitions would remain tiny;
- no process will create, monitor, archive, or remove partitions.

Partitioning adds relations, indexes, statistics, locks, planning work, automation, and failure modes. Use it when those costs buy a clear lifecycle or query benefit.

## 2. Do not distribute by timestamp by default

Hash distribution on a timestamp scatters adjacent time ranges across shards. Most time-series queries use time ranges, so timestamp is usually a poor Citus distribution key.

Prefer:

- `tenant_id` for multi-tenant data;
- `device_id`, `host_id`, `account_id`, `repository_id`, or another stable entity key for real-time/event data;
- PostgreSQL `PARTITION BY RANGE (<TIME_COLUMN>)` for time locality.

A timestamp distribution key is defensible only when the actual workload uses exact timestamp equality as the locality boundary and skew plus join behavior have been proven acceptable. That is uncommon.

## 3. Choose the PostgreSQL partition method

### Range partitioning

Best for:

- timestamps and dates;
- monotonically increasing IDs with lifecycle ranges;
- archival and retention windows;
- hot/cold storage transitions.

```sql
CREATE TABLE <SCHEMA>.<PARENT_TABLE> (
  <DIST_COLUMN> bigint NOT NULL,
  event_id bigint NOT NULL,
  <PARTITION_COLUMN> timestamptz NOT NULL,
  payload jsonb NOT NULL,
  PRIMARY KEY (<DIST_COLUMN>, event_id, <PARTITION_COLUMN>)
) PARTITION BY RANGE (<PARTITION_COLUMN>);
```

### List partitioning

Best for a small, stable set of operational categories that need separate lifecycle or storage treatment, such as region or data class.

Risks:

- category growth requires DDL;
- a default partition can silently accumulate unexpected values;
- list partitioning by a low-cardinality field does not replace a good Citus distribution key.

### Hash partitioning inside PostgreSQL

Use rarely in a Citus table. Citus already hash-distributes rows across logical shards. A second PostgreSQL hash layer can multiply relations without adding useful pruning or lifecycle control.

It may be justified for specialized local tables or for a measured per-shard hotspot, but requires benchmark evidence and a clear operational owner.

### Multi-level partitioning

Do not assume multi-level partition trees are supported for distributed tables. Current Citus documentation identifies distribution of multi-level partitioned tables as unsupported. Even when a particular layout can be created, it can generate severe relation-count, DDL, lock, and planning overhead.

Use one partition dimension unless capability checks and a representative test prove the exact hierarchy safe.

## 4. Greenfield creation order

For a new table, use this order:

1. create the partitioned parent;
2. distribute the parent;
3. create or attach leaf partitions;
4. create/verify indexes and constraints;
5. pre-create future partitions;
6. test routing, pruning, writes, and retention;
7. automate lifecycle jobs.

Example:

```sql
CREATE TABLE <SCHEMA>.events (
  tenant_id bigint NOT NULL,
  event_id bigint NOT NULL,
  event_time timestamptz NOT NULL,
  event_type text NOT NULL,
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

Create a partition manually:

```sql
CREATE TABLE <SCHEMA>.events_2026_08
PARTITION OF <SCHEMA>.events
FOR VALUES FROM ('2026-08-01 00:00:00+00')
         TO   ('2026-09-01 00:00:00+00');
```

Or, when available, create time partitions with Citus helpers:

```sql
SELECT create_time_partitions(
  table_name         := '<SCHEMA>.events',
  partition_interval := '<PARTITION_INTERVAL>',
  start_from         := <START_BOUND>,
  end_at             := <END_BOUND>
);
```

Verify the installed signature in `pg_proc`; argument types and procedure/function form can vary by release.

## 5. Existing partitioned table migration

Do not treat a populated partition tree like a new empty parent. First inventory:

- parent and all descendants;
- partition strategy, keys, bounds, and default partition;
- total rows and bytes per child;
- indexes and constraints on parent and children;
- foreign keys, triggers, generated columns, identity/sequence use;
- access methods;
- existing invalid or detached partitions;
- application writes during migration;
- target distribution key available on every child row.

Then choose one of these approaches:

### A. In-place conversion

Potentially simplest, but can move large amounts of data and acquire locks. Use only when the installed Citus version explicitly supports the existing hierarchy and the maintenance window is acceptable.

### B. Shadow distributed partition tree

Create a new parent, distribute it, create partitions, copy/backfill data, synchronize changes, validate, and cut over. This gives clearer checkpoints and rollback at the cost of temporary storage and synchronization complexity.

### C. Period-by-period migration

Move closed historical periods first, then handle the active partition during cutover. Useful when history is immutable and the active window is much smaller.

Never run `truncate_local_data_after_distributing_table()` merely because a conversion appears complete. Verify worker-side row counts, checksums or aggregates, constraints, application reads, and rollback first.

## 6. Choose the partition interval scientifically

Inputs:

- rows and bytes ingested per hour/day;
- retained history;
- typical and maximum query windows;
- percentage of queries limited to recent data;
- index size and working-set target;
- autovacuum/analyze behavior;
- expected delete/archive frequency;
- maintenance and DDL lock budget;
- number of Citus shards;
- indexes per partition;
- current and future worker count;
- planning latency and catalog size tolerance.

### A practical method

1. Calculate data per candidate interval.
2. Estimate relations created across the cluster.
3. Check whether common queries prune most partitions.
4. Check whether one partition is a useful retention/archival unit.
5. Check whether indexes for the active partitions fit the intended memory budget.
6. Benchmark planning and execution with the projected partition count.
7. Rehearse create, attach, detach, drop, and conversion operations under concurrent load.

### Relation-count budget

At minimum, estimate:

```text
logical leaf partitions
× shard count per colocation group
× physical placements/replication factor
× (table relation + indexes + toast-related relations)
```

A simplified review metric is:

```text
shard_count × active_partition_count × index_count
```

It is not an exact catalog relation count, but it quickly exposes explosive designs.

Example:

```text
128 shards × 730 daily partitions × 5 indexes
= 467,200 shard-partition-index combinations
```

That design needs strong evidence. A monthly interval or lower shard count might provide the same retention and pruning benefit with far less overhead.

### Interval heuristics

These are questions, not defaults:

- hourly: very high ingest, short retention, narrow queries, frequent lifecycle changes;
- daily: moderate/high ingest, queries measured in hours/days, retention in weeks/months;
- weekly: moderate ingest and week-scale queries;
- monthly: long retention, month-scale reports, lower DDL/relation overhead;
- quarterly/yearly: archival or low-volume history, not a good active-write default.

Choose from measured data, not the label “time series.”

## 7. Integrity constraints across sharding and partitioning

Two rules can apply simultaneously:

1. Citus distributed uniqueness normally requires the distribution column.
2. PostgreSQL uniqueness on a partitioned parent normally requires the partition key columns.

Therefore, a natural key on a range-partitioned distributed table often includes both:

```sql
PRIMARY KEY (<DIST_COLUMN>, entity_id, <PARTITION_COLUMN>)
```

Do not add the time key blindly to every business identity. Decide whether the logical identity is:

- unique only within one time period;
- globally unique by application-generated identifier;
- enforced through a separate lookup/idempotency table;
- validated by application workflow;
- unsuitable for partitioning without a redesign.

Foreign keys must be reviewed against:

- table types;
- colocation;
- matching distribution-key type;
- partitioned-parent support;
- referenced uniqueness;
- cross-schema limitations.

Add replica identity suitable for online shard movement and logical replication. A primary key is usually the cleanest option when compatible with the model.

## 8. Index strategy

Indexes declared on a partitioned parent propagate to partitions according to PostgreSQL behavior, while Citus propagates distributed DDL to shard relations. Verify the final worker indexes rather than assuming propagation succeeded.

Common patterns:

### Tenant/entity timeline

```sql
CREATE INDEX ON <SCHEMA>.events
(<DIST_COLUMN>, <PARTITION_COLUMN> DESC, event_id DESC);
```

### Filtered active-state index

```sql
CREATE INDEX ON <SCHEMA>.events
(<DIST_COLUMN>, <PARTITION_COLUMN> DESC)
WHERE state = 'active';
```

### BRIN for large append-only time ranges

```sql
CREATE INDEX ON <SCHEMA>.events
USING brin (<PARTITION_COLUMN>);
```

A BRIN index is useful when physical order correlates with time and queries scan ranges. It is not a replacement for the routing key or selective indexes.

Avoid creating every possible index on every historical partition. Historical columnar partitions may not support or benefit from the same index set as hot heap partitions.

## 9. Verify both shard pruning and partition pruning

Use representative parameters:

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
SELECT *
FROM <SCHEMA>.events
WHERE <DIST_COLUMN> = <TENANT_VALUE>
  AND <PARTITION_COLUMN> >= <START_BOUND>
  AND <PARTITION_COLUMN> <  <END_BOUND>;
```

Check:

- task/shard count;
- partitions or subplans removed;
- worker scans and indexes;
- planning time;
- rows removed by filter;
- network/intermediate-result behavior;
- whether generic prepared plans reduce pruning;
- whether expressions hide the partition key.

Less prune-friendly:

```sql
WHERE date_trunc('day', event_time) = $1
```

More prune-friendly when semantically equivalent:

```sql
WHERE event_time >= $1
  AND event_time <  $1 + interval '1 day'
```

Do not rewrite without testing time zone and boundary semantics.

## 10. Partition lifecycle

A complete lifecycle includes:

1. pre-create future partitions;
2. detect missing, overlapping, or unexpected bounds;
3. monitor the active partition and default partition;
4. analyze new partitions after significant load;
5. convert or archive closed partitions when appropriate;
6. drop/detach data past retention;
7. validate that jobs continue after deploys, failovers, upgrades, or owner changes.

### Inspect Citus-created time partitions

When available:

```sql
SELECT parent_table,
       partition,
       from_value,
       to_value,
       access_method
FROM time_partitions
WHERE parent_table = '<SCHEMA>.events'::regclass
ORDER BY from_value;
```

Inspect the actual columns in the installed view before selecting them.

### Create future partitions

```sql
SELECT create_time_partitions(
  table_name         := '<SCHEMA>.events',
  partition_interval := '<PARTITION_INTERVAL>',
  end_at             := now() + <FUTURE_HORIZON>
);
```

### Drop old partitions

When the installed object is a procedure:

```sql
CALL drop_old_time_partitions(
  '<SCHEMA>.events',
  now() - <RETENTION_INTERVAL>
);
```

Before deletion:

- preview eligible partitions with bounds and sizes;
- confirm legal/business retention;
- confirm backup or archival policy;
- check foreign-key and dependency behavior;
- test lock impact;
- define a restore path.

Dropping a partition is `DESTRUCTIVE`, even when it is fast.

## 11. Default partitions

A default partition can prevent insert failures when a future range is missing, but it can conceal broken lifecycle automation.

Use a default partition only when:

- temporary catch-all behavior is explicitly desired;
- it is monitored for nonzero rows;
- rows are moved into proper partitions quickly;
- new partition creation accounts for validation scans and locks on the default partition.

Monitor:

```sql
SELECT count(*)
FROM <SCHEMA>.<DEFAULT_PARTITION>;
```

Exact counts can be expensive. For frequent monitoring, use statistics or a bounded time predicate when possible.

## 12. Attach and detach operations

Attaching a preloaded table can reduce load time, but PostgreSQL may scan it to validate the partition bound. A matching validated `CHECK` constraint can sometimes avoid that scan.

Plan for locks on:

- the parent;
- the table being attached/detached;
- a default partition;
- related partition/index objects;
- foreign-key relationships.

Use `DETACH PARTITION CONCURRENTLY` only after confirming PostgreSQL and Citus compatibility for the exact hierarchy and operational goal. A lower PostgreSQL lock level does not guarantee that the overall distributed operation is safe or online.

## 13. Automation with pg_cron or an external scheduler

Automation must be idempotent and observable.

Typical jobs:

- create partitions ahead of time;
- analyze newly loaded partitions;
- convert immutable partitions to columnar;
- detach/archive or drop expired partitions;
- validate partition gaps and default-partition rows.

Example capability-gated schedule:

```sql
SELECT cron.schedule(
  'create-<TABLE>-partitions',
  '<CRON_EXPRESSION>',
  $$
  SELECT create_time_partitions(
    table_name         := '<SCHEMA>.events',
    partition_interval := '<PARTITION_INTERVAL>',
    end_at             := now() + <FUTURE_HORIZON>
  );
  $$
);
```

Operational requirements:

- stable job owner and privileges;
- correct database target;
- alerts for failure and unexpected duration;
- no overlapping maintenance jobs;
- deploy and upgrade checks;
- documented manual recovery.

## 14. Hot/cold hybrid design

A common design:

- newest partitions: heap, indexed, update/delete allowed;
- older immutable partitions: columnar, fewer/no indexes, compressed scans;
- expired partitions: detached/archive or dropped.

Capability-gated conversion:

```sql
CALL alter_old_partitions_set_access_method(
  '<SCHEMA>.events',
  now() - <HOT_WINDOW>,
  'columnar'
);
```

Validate:

- no late updates/deletes target converted partitions;
- conversion does not remove required indexes without an approved plan;
- query plans remain correct across mixed access methods;
- bulk-load transaction sizes produce useful columnar stripes;
- restore and retention procedures handle both access methods.

See `06-columnar-and-hybrid-storage.md`.

## 15. Late-arriving and out-of-order data

Define an explicit policy:

- allow late writes into older heap partitions;
- keep a wider hot window before columnar conversion;
- route late events to a correction table;
- reopen/convert a partition through a controlled process;
- reject data outside a business boundary.

Do not convert a partition to append-only columnar storage while normal application traffic can still update or delete it.

Track late-arrival percentiles, not only the average delay.

## 16. Rollups and retention tiers

For long retention, consider multiple tiers:

1. raw high-resolution data;
2. hourly/daily rollups;
3. historical columnar raw data or archive;
4. deletion after legal/business retention.

Design rollup keys to preserve useful locality:

```text
(<DIST_COLUMN>, bucket_start, dimensions...)
```

Make rollups idempotent with a unique key and safe upsert pattern. Validate source completeness before dropping raw partitions.

## 17. Partition monitoring queries

### Inventory parent and children

```sql
SELECT parent_ns.nspname AS parent_schema,
       parent.relname AS parent_table,
       child_ns.nspname AS child_schema,
       child.relname AS child_table,
       pg_get_expr(child.relpartbound, child.oid) AS partition_bound,
       am.amname AS access_method,
       pg_total_relation_size(child.oid) AS total_bytes
FROM pg_inherits AS i
JOIN pg_class AS parent ON parent.oid = i.inhparent
JOIN pg_namespace AS parent_ns ON parent_ns.oid = parent.relnamespace
JOIN pg_class AS child ON child.oid = i.inhrelid
JOIN pg_namespace AS child_ns ON child_ns.oid = child.relnamespace
LEFT JOIN pg_am AS am ON am.oid = child.relam
WHERE parent.oid = '<SCHEMA>.<PARENT_TABLE>'::regclass
ORDER BY child.relname;
```

### Estimate live/dead tuples

```sql
SELECT schemaname,
       relname,
       n_live_tup,
       n_dead_tup,
       last_analyze,
       last_autoanalyze,
       last_vacuum,
       last_autovacuum
FROM pg_stat_user_tables
WHERE relid IN (
  SELECT inhrelid
  FROM pg_inherits
  WHERE inhparent = '<SCHEMA>.<PARENT_TABLE>'::regclass
)
ORDER BY relname;
```

On a Citus cluster, coordinator statistics may not represent every physical shard relation. Use worker-aware diagnostics when exact distributed maintenance state is required.

## 18. Anti-patterns

- Distribute by time and partition by tenant without workload evidence.
- Use daily partitions for ten years with hundreds of shards and many indexes.
- Create partitions only after an insert fails.
- Keep an unmonitored default partition forever.
- Drop partitions without a retention/restore decision.
- Put `now()` or complex wrappers around the partition key and assume pruning.
- Create different, incompatible partition boundaries on tables expected to use partition-wise behavior.
- Add partitioning to hide a bad distribution key.
- Convert history to columnar while late mutations remain common.
- Assume parent-level index/constraint DDL succeeded on every worker without verification.
- Use multi-level partitioning because it looks organized rather than because it was capability-tested and benchmarked.

## 19. Partition design review checklist

- [ ] The distribution key and partition key solve different, explicit problems.
- [ ] Hot queries include the distribution key when possible.
- [ ] Hot queries use prune-friendly partition predicates.
- [ ] Partition interval is based on data and workload measurements.
- [ ] Projected shard × partition × index relation count is acceptable.
- [ ] Primary/unique constraints satisfy both Citus and PostgreSQL partition rules.
- [ ] Replica identity supports planned data movement.
- [ ] Future partitions are created ahead of writes.
- [ ] Default-partition behavior is explicit and monitored.
- [ ] Create/attach/detach/drop lock behavior was tested.
- [ ] Retention, archival, and restore were rehearsed.
- [ ] Late-arriving data policy is documented.
- [ ] Hot/cold conversion happens only after immutability criteria are met.
- [ ] Multi-level partitioning is avoided unless explicitly supported and measured.
- [ ] Planning time and catalog growth were tested at projected scale.
