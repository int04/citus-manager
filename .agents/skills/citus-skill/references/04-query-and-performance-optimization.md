# Citus Query and Performance Optimization

Use `03-partitioning-and-time-series.md` for partition-specific planning and pruning, and `05-dml-transactions-and-ingestion.md` for write-path design.

Optimize Citus in this order: **data model → query routing → indexes and statistics → connections → shards and rebalancing → hardware**. Do not begin by increasing GUC values or adding workers when hot queries are missing the distribution key.

## 1. Establish a baseline before tuning

Collect measurements over the same representative time window:

- throughput: queries, transactions, or rows per second;
- p50, p95, and p99 latency by endpoint or query;
- top queries by total time and mean time;
- single-shard versus multi-shard ratio;
- CPU, memory, disk latency/IOPS, and network use on the coordinator and every worker;
- active and idle connections plus connection failures;
- shard count, shard size, and node-level skew;
- locks, WAL, replication slots, and background jobs;
- autovacuum and analyze status.

### Query statistics

```sql
SELECT queryid,
       calls,
       total_exec_time,
       mean_exec_time,
       rows,
       shared_blks_hit,
       shared_blks_read,
       temp_blks_written,
       left(query, 300) AS query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 50;
```

```sql
SELECT *
FROM citus_stat_statements
ORDER BY calls DESC
LIMIT 50;
```

```sql
SELECT *
FROM citus_stat_counters;
```

If counters are not enabled:

```sql
ALTER DATABASE <DB_NAME>
SET citus.enable_stat_counters = on;
```

Reconnect before measuring. Depending on the version, counters may reset after a restart.

### Resource snapshot

```sql
SELECT pid,
       usename,
       state,
       wait_event_type,
       wait_event,
       now() - query_start AS runtime,
       left(query, 300) AS query
FROM pg_stat_activity
WHERE datname = current_database()
ORDER BY query_start NULLS LAST;
```

```sql
SELECT * FROM citus_remote_connection_stats();
SELECT * FROM citus_lock_waits;
```

Do not compare before and after results when workload, cache state, or data volume differs materially.

---

## 2. Classify the query path

### 2.1. Single-shard/router query

Typical characteristics:

- equality predicate on the distribution column;
- joins preserve one distribution value;
- low task count, often one shard;
- latency close to plain PostgreSQL on one worker.

Example:

```sql
SELECT *
FROM app.records
WHERE tenant_id = $1
  AND record_id = $2;
```

This is the preferred OLTP path.

### 2.2. Colocated query

Multiple tables share the same distribution key and colocation group, allowing joins to execute locally on workers.

```sql
SELECT r.record_id, i.item_no, i.value
FROM app.records AS r
JOIN app.record_items AS i
  ON i.tenant_id = r.tenant_id
 AND i.record_id = r.record_id
WHERE r.tenant_id = $1;
```

Include distribution-key equality in both join and filter predicates whenever the logic permits it.

### 2.3. Multi-shard parallel query

No single distribution value is known, but scans or aggregates can run in parallel across many shards.

```sql
SELECT category_code, count(*)
FROM app.records
WHERE created_at >= $1
GROUP BY category_code;
```

This can be appropriate for analytics, but task count, connections, and intermediate results must be controlled.

### 2.4. Repartition or cross-shard query

The join key does not match the distribution key, or the tables are not colocated. Citus must transfer or reshuffle data between nodes.

This is often the most expensive query path. Before adding resources, consider:

- adding a tenant/entity predicate;
- denormalizing the distribution key;
- colocating the tables;
- making a small lookup table a reference table;
- creating a summary or materialized table;
- separating online queries from offline analytics.

### 2.5. Coordinator-heavy query

Workers return a large amount of data to the coordinator for final sorting, `DISTINCT`, window functions, or aggregation.

Common signs:

- low worker CPU but high coordinator CPU, network, or temporary-file usage;
- large `Tuple data received` values in the plan;
- heavy temporary-file activity;
- large result or intermediate data sets.

Reduce data before it reaches the coordinator through filtering, partial aggregation, pre-aggregation, or a different data model.

---

## 3. Use EXPLAIN correctly

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS, SETTINGS)
<QUERY>;
```

For queries with uneven task performance:

```sql
BEGIN;
SET LOCAL citus.explain_all_tasks = on;
SET LOCAL citus.explain_analyze_sort_method = 'execution-time';

EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
<QUERY>;
ROLLBACK;
```

Inspect:

- task and shard count;
- fastest and slowest task times;
- estimated rows versus actual rows;
- index scans versus sequential scans;
- sort or hash spills;
- bytes or tuples sent to the coordinator;
- connection setup time;
- outlier shards.

`EXPLAIN ANALYZE` executes the query. Do not run it casually on production DML or extremely expensive statements.

---

## 4. Optimize the query before tuning the executor

### 4.1. Pass the distribution key from the API to the database

Poor:

```sql
SELECT *
FROM app.records
WHERE record_id = $1;
```

Better when the application knows the tenant:

```sql
SELECT *
FROM app.records
WHERE tenant_id = $1
  AND record_id = $2;
```

This both routes to the correct shard and strengthens tenant isolation.

### 4.2. Preserve the key in joins

Poor:

```sql
... JOIN app.record_items i
       ON i.record_id = r.record_id
```

Better:

```sql
... JOIN app.record_items i
       ON i.tenant_id = r.tenant_id
      AND i.record_id = r.record_id
```

### 4.3. Keep transactions single-shard

Multiple statements for one tenant or entity can remain on one worker, reducing two-phase-commit and network overhead.

Do not combine many tenants into one transaction merely to reduce round trips if that creates a large multi-shard transaction.

### 4.4. Use a distributed function for multi-statement logic

When a function always operates on one distribution value, it can be routed by that argument:

```sql
SELECT create_distributed_function(
  'app.process_entity(bigint,bigint)',
  'tenant_id',
  colocate_with => 'app.records'
);
```

Use this only when the function truly restricts all data access to that argument.

### 4.5. Enable aggregate pushdown

Keeping the distribution key in grouping and join conditions lets workers aggregate locally:

```sql
SELECT tenant_id, category_code, count(*)
FROM app.records
WHERE tenant_id = $1
GROUP BY tenant_id, category_code;
```

For frequently repeated cluster-wide aggregates, create a rollup table at the required grain.

### 4.6. Preserve the distribution key in `INSERT ... SELECT`

When source and target are colocated and the distribution value is preserved:

```sql
INSERT INTO app.record_daily(tenant_id, day, count_value)
SELECT tenant_id,
       date_trunc('day', created_at)::date,
       count(*)
FROM app.records
WHERE created_at >= $1
  AND created_at < $2
GROUP BY tenant_id, date_trunc('day', created_at)::date;
```

Inspect the plan and version-specific limitations before using this pattern for large pipelines.

### 4.7. Pagination

Tenant-scoped keyset pagination is usually better than a large offset:

```sql
SELECT *
FROM app.records
WHERE tenant_id = $1
  AND (created_at, record_id) < ($2, $3)
ORDER BY created_at DESC, record_id DESC
LIMIT $4;
```

Supporting index:

```sql
CREATE INDEX ix_records_tenant_created_id
ON app.records(tenant_id, created_at DESC, record_id DESC);
```

---

## 5. Indexes on distributed tables

Distributed tables still use PostgreSQL indexes on every shard. Tune them from actual query patterns.

### 5.1. Router query

```sql
CREATE INDEX ix_records_tenant_record
ON app.records(tenant_id, record_id);
```

Do not add a duplicate index when the primary key already covers `(tenant_id, record_id)`.

### 5.2. Tenant-scoped timeline or range query

```sql
CREATE INDEX ix_records_tenant_created
ON app.records(tenant_id, created_at DESC);
```

### 5.3. Partial index for a hot status

```sql
CREATE INDEX ix_records_tenant_pending
ON app.records(tenant_id, created_at)
WHERE status = 'pending';
```

This helps only when the predicate is stable and the query matches it.

### 5.4. JSONB

```sql
CREATE INDEX ix_records_payload_gin
ON app.records
USING gin(payload);
```

When write cost is high, consider an expression or partial index on the hot JSON field instead of indexing the whole document.

### 5.5. BRIN by time

For very large append-only tables whose physical order correlates with time:

```sql
CREATE INDEX ix_events_time_brin
ON app.events
USING brin(event_time);
```

Benchmark on representative shards. BRIN does not replace B-tree for highly selective lookups.

### 5.6. Audit worker indexes

```sql
SELECT *
FROM run_command_on_workers($cmd$
  SELECT schemaname,
         relname,
         indexrelname,
         idx_scan,
         idx_tup_read,
         idx_tup_fetch,
         pg_relation_size(indexrelid) AS bytes
  FROM pg_stat_user_indexes
$cmd$);
```

Do not drop an index solely because `idx_scan = 0` during a short measurement window. Review constraints, failover, reporting, and seasonal workload patterns.

---

## 6. Statistics, vacuum, and estimates

After a bulk load, resharding, or a major distribution shift:

```sql
ANALYZE app.records;
```

When bloat or dead tuples are significant:

```sql
VACUUM (ANALYZE) app.records;
```

Inspect statistics:

```sql
SELECT *
FROM citus_stats
WHERE tablename = 'records'
ORDER BY attname;
```

Inspect workers:

```sql
SELECT *
FROM run_command_on_workers($cmd$
  SELECT schemaname,
         relname,
         n_live_tup,
         n_dead_tup,
         last_autovacuum,
         last_autoanalyze
  FROM pg_stat_user_tables
  ORDER BY n_dead_tup DESC
  LIMIT 50
$cmd$);
```

When estimates are poor because of tenant skew, consider increasing the statistics target on key columns:

```sql
ALTER TABLE app.records
ALTER COLUMN tenant_id
SET STATISTICS <TARGET>;

ANALYZE app.records;
```

Do not raise statistics targets across every table and column without measuring planning and analyze cost.

---

## 7. Connection fan-out

Citus has two connection layers:

1. application/client → coordinator or query node;
2. coordinator or query node → workers.

PgBouncer addresses the client layer only. Multi-shard queries still create internal worker connections.

### 7.1. Measure connections

```sql
SELECT * FROM citus_remote_connection_stats();
```

```sql
SELECT client_addr,
       usename,
       state,
       count(*)
FROM pg_stat_activity
GROUP BY client_addr, usename, state
ORDER BY count(*) DESC;
```

### 7.2. Key GUCs

#### `citus.max_shared_pool_size`

Limits total coordinator-to-worker connections per remote node across sessions. Use it to protect workers from connection storms.

#### `citus.local_shared_pool_size`

Limits internal connections to the local node when the coordinator also stores shards or runs single-node Citus.

#### `citus.max_adaptive_executor_pool_size`

Limits worker connections available to one session. This is useful for separating interactive and reporting workloads.

```sql
ALTER ROLE reporting_user
SET citus.max_adaptive_executor_pool_size = 4;
```

#### `citus.executor_slow_start_interval`

Controls the delay before opening additional connections to the same worker. Short queries often benefit from slow start; long queries may require a different benchmarked value.

#### `citus.max_cached_conns_per_worker`

Controls cached connections per backend and worker. A small increase may reduce latency but also increases the number of worker connections retained.

#### `citus.force_max_query_parallelization`

Use only for a deliberately measured query or transaction. Enabling it globally can reduce total system throughput.

### 7.3. Calculate headroom

On each worker:

```text
max_connections
- reserved/admin
- autovacuum/background
- replication/rebalancing
- monitoring
= usable_for_clients_and_citus
```

Do not configure `citus.max_shared_pool_size` to consume all of `max_connections`; always preserve headroom.

### 7.4. Separate workloads

Set role-specific GUCs where useful:

- latency-sensitive API: moderate pool limits;
- reporting: lower limits so reports cannot monopolize workers;
- batch or ETL: session-specific settings during off-peak windows;
- administration and rebalancing: separate reserved headroom.

---

## 8. Shard and node skew

### 8.1. Shard-size skew

Use `MAX(shard_size)` to avoid double counting when a shard has multiple placements:

```sql
WITH per_shard AS (
  SELECT table_name,
         shardid,
         MAX(shard_size)::numeric AS shard_bytes
  FROM citus_shards
  WHERE table_name = '<SCHEMA>.<TABLE>'::regclass
  GROUP BY table_name, shardid
)
SELECT count(*) AS shard_count,
       pg_size_pretty(avg(shard_bytes)::bigint) AS avg_size,
       pg_size_pretty(max(shard_bytes)::bigint) AS max_size,
       round(max(shard_bytes) / NULLIF(avg(shard_bytes), 0), 2) AS max_to_avg_ratio
FROM per_shard;
```

### 8.2. Node imbalance

```sql
SELECT nodename,
       nodeport,
       count(*) AS placements,
       pg_size_pretty(sum(shard_size)::bigint) AS bytes
FROM citus_shards
GROUP BY nodename, nodeport
ORDER BY sum(shard_size) DESC;
```

### 8.3. Tenant skew

This query may be expensive. Run it in staging, off-peak, or against a representative sample:

```sql
SELECT tenant_id,
       count(*) AS rows,
       sum(pg_column_size(t)) AS approx_bytes
FROM app.records AS t
GROUP BY tenant_id
ORDER BY approx_bytes DESC
LIMIT 50;
```

### 8.4. Hot tenants

Evenly sized shards do not imply evenly distributed traffic. Combine:

- application metrics by tenant;
- `citus_stat_tenants` when supported;
- query tags or `application_name`;
- CPU and latency by shard or node.

Escalating responses:

1. fix indexes or queries;
2. add caching or rollups;
3. isolate the tenant into a new shard;
4. move the shard to a more suitable worker;
5. separate the tenant or database, or change the model when one tenant exceeds the original design.

---

## 9. Bulk ingestion and write throughput

### 9.1. Prefer `COPY` or batches over row-by-row inserts

```sql
COPY app.records(tenant_id, record_id, created_at, payload)
FROM STDIN WITH (FORMAT csv);
```

The input must include the distribution column. Use batches large enough to reduce round trips but not so large that they create transaction or WAL spikes.

### 9.2. Avoid one enormous multi-shard transaction

Split work by tenant, time window, or checkpointed batch size. This reduces locks, two-phase-commit overhead, and rollback cost.

### 9.3. Initial load

A typical workflow is:

- create the table or partitions;
- distribute them with the intended layout;
- load data;
- build nonessential indexes after the load when that is faster;
- run `ANALYZE`;
- validate row counts, checksums, and queries.

Do not write directly to physical shard tables unless an official tool or runbook requires it, because doing so can bypass metadata and routing rules.

### 9.4. Commit protocol

`citus.multi_shard_commit_protocol` affects durability and performance for multi-shard `COPY`. Do not move away from the safe default merely for speed unless failure modes and recovery behavior have been tested.

---

## 10. Rebalancer tuning

### 10.1. Always preview first

```sql
SELECT *
FROM get_rebalance_table_shards_plan();
```

Review:

- move count;
- total bytes;
- source and target nodes;
- colocation groups moved together;
- reference-table copies;
- node capacity.

### 10.2. Strategy

`by_disk_size` is usually a strong default because it accounts for shard sizes.

Use `by_shard_count` only when:

- shards are similarly sized;
- traffic is similarly distributed;
- workers have similar capacity;
- no shards are pinned.

### 10.3. Parallel transfer

Newer versions may support parallel colocated-shard and reference-table transfers. Before enabling them, inspect:

- the actual function signature;
- `max_worker_processes`;
- Citus background task executors;
- replication slots and WAL senders;
- disk read/write and network headroom;
- replica identity.

More parallelism can shorten the operation while worsening application latency.

### 10.4. Monitor the operation

```sql
SELECT * FROM citus_rebalance_status();
SELECT * FROM pg_replication_slots;
SELECT * FROM pg_stat_activity;
```

Monitor outside SQL as well:

- disk queue depth and latency;
- network throughput and retransmissions;
- WAL growth;
- free disk space;
- API p95 and p99 latency.

---

## 11. Coordinator bottlenecks

Typical signs:

- workers have spare capacity while coordinator CPU is high;
- many multi-shard queries return large data sets;
- planning, aggregation, sorting, or temporary files are concentrated on the coordinator;
- the connection endpoint is saturated;
- local tables are large or write-heavy.

Address them in this order:

1. route queries to a single shard;
2. push down filters and aggregation;
3. reduce result and intermediate data;
4. convert large local tables to distributed tables;
5. pool client connections;
6. separate reporting or query endpoints when the topology supports it;
7. increase coordinator resources only after the query and data model are sound.

Do not use a larger coordinator to hide a fundamentally cross-shard data model.

---

## 12. Worker sizing and topology

Prefer similarly sized workers so the rebalancer can reason about capacity more predictably. When worker capacities differ:

- use capacity-aware or custom strategies when supported;
- avoid balancing solely by shard count;
- monitor CPU and disk, not only placement count;
- prevent one slow worker from defining tail latency for multi-shard queries.

Storage guidance:

- use SSD or NVMe for OLTP;
- preserve enough free space for shard movement, WAL, and temporary files;
- disk latency is often more important than nominal capacity.

Network guidance:

- keep workers in the same region or datacenter when possible;
- latency and bandwidth directly affect repartitioning and rebalancing;
- avoid public-network paths between database nodes.

---

## 13. Benchmarking workflow

For every proposed change:

1. record the baseline and query parameters;
2. change only one factor;
3. warm the workload;
4. run long enough to produce meaningful p95 and p99 values;
5. measure both the target query and background workload;
6. inspect resource use on every node;
7. save before-and-after plans;
8. roll back if total cluster throughput or tail latency becomes worse.

Do not draw conclusions from one cold run or one isolated query.

## 14. Quick optimization checklist

- Do hot queries include the distribution key?
- Do joins include the distribution key in the join predicate?
- Are related tables in the same colocation group?
- Are small shared lookups reference tables?
- Has a local table become a coordinator bottleneck?
- Do primary and unique keys include the distribution column?
- Does the shard count fit the worker, CPU, and connection budget?
- Is there shard, tenant, or node skew?
- Do indexes and statistics match the actual queries?
- Is the coordinator receiving too many intermediate rows?
- Are internal connections reaching their limit?
- Is rebalancing competing for disk I/O or WAL?
- Were p95 and p99 measured before and after the change?
