# Advanced SQL, Analytics, Extensions, and Compatibility

Use this reference when the request involves SQL behavior beyond basic router queries, analytical aggregates, advanced joins, views, triggers, sequences, row-level security, PostgreSQL extensions, or a feature that might be supported only for particular query shapes or Citus releases.

Citus supports a large PostgreSQL surface, but support and execution quality depend on table types, distribution, colocation, query shape, and installed version. Never replace an exact capability test with a generic statement that “PostgreSQL supports it.”

## 1. Capability-first workflow

For any advanced feature:

1. capture the exact SQL, schema, table types, and parameters;
2. identify the distribution and partition columns of every Citus table;
3. identify colocation groups and reference/local tables;
4. inspect PostgreSQL and Citus versions;
5. search the installed function/GUC/catalog surface;
6. run `EXPLAIN (VERBOSE, COSTS, SETTINGS)` on a safe environment;
7. run a representative correctness test;
8. run `EXPLAIN (ANALYZE, VERBOSE, BUFFERS)` only where execution is safe;
9. record whether execution is router, colocated, multi-shard, repartition, or coordinator-heavy;
10. benchmark before adopting the feature in a hot path.

Useful metadata:

```sql
SELECT table_name,
       citus_table_type,
       distribution_column,
       colocation_id,
       shard_count,
       access_method
FROM citus_tables
ORDER BY table_name;
```

When a high-level view is unavailable, use `pg_dist_partition`, `pg_dist_colocation`, `pg_class`, and runtime capability discovery.

## 2. Aggregate execution model

Classify an aggregate into one of these paths.

### 2.1. Full pushdown

The grouping and joins align with the distribution key and colocation. Each worker computes complete groups locally, and the receiving node combines a small result.

Typical favorable shape:

```sql
SELECT <DIST_COLUMN>,
       date_trunc('day', <EVENT_TIME>) AS bucket,
       count(*) AS event_count,
       sum(<MEASURE>) AS total_measure
FROM <SCHEMA>.<DISTRIBUTED_TABLE>
WHERE <DIST_COLUMN> = <TENANT_VALUE>
  AND <EVENT_TIME> >= <START_TIME>
  AND <EVENT_TIME> < <END_TIME>
GROUP BY <DIST_COLUMN>, bucket;
```

### 2.2. Partial aggregate plus finalization

Workers compute partial states; the receiving node combines them. This can be efficient when partial states are small and the number of output groups is bounded.

Measure:

- rows and bytes returned by each task;
- number of groups per shard;
- receiving-node memory and temporary files;
- final aggregation time;
- skew in groups or input rows.

### 2.3. Coordinator-heavy aggregate

Large intermediate result sets, high-cardinality groupings, unsupported aggregate decomposition, or poor query shape can force substantial receiving-node work.

Before increasing memory or parallelism:

1. restrict by distribution key where possible;
2. pre-aggregate on workers;
3. add time or domain filters;
4. create an incremental rollup table;
5. use a mergeable sketch only when approximation is acceptable;
6. isolate analytical traffic from latency-sensitive OLTP.

## 3. Exact and approximate distinct counts

Exact `count(DISTINCT ...)` can require large intermediate state or data movement when the distinct key is not local to each group.

Evaluate:

- whether the distinct key includes or functionally depends on the distribution key;
- whether the query can group by distribution key first;
- whether an exact answer is required;
- error tolerance and confidence requirements;
- update/merge behavior of the selected sketch;
- extension availability on all relevant nodes.

For approximate cardinality, a mergeable HyperLogLog implementation can reduce intermediate state. Treat this as an architectural decision, not a transparent replacement:

- define the accepted relative error;
- version the sketch representation;
- verify extension installation and object propagation;
- test merge compatibility across upgrades;
- retain an exact validation sample.

Do not recommend an approximation without stating the error contract to the user.

## 4. Approximate percentiles and distributions

Percentiles over very large distributed data sets can be expensive because exact ordering is global. Mergeable sketches such as t-digest can be appropriate when:

- approximate percentiles are acceptable;
- the extension exists and is compatible on every required node;
- partial states can be merged correctly;
- compression/accuracy is benchmarked with representative distributions;
- application and reporting layers label the result as approximate.

Validation should compare approximate p50/p95/p99 values against exact calculations on a bounded sample and on adversarial distributions, not only uniform data.

## 5. Rollup-table engineering

Use rollups when the same expensive aggregate is queried repeatedly over mostly append-only input.

A robust rollup design states:

- source table and immutable event identity;
- distribution and partition keys of source and rollup;
- aggregation grain;
- additive, algebraic, or holistic aggregate behavior;
- late-arrival correction window;
- idempotency key;
- refresh schedule or incremental cursor;
- backfill and rebuild procedure;
- reconciliation query;
- retention and storage method.

Prefer a rollup that preserves the source distribution key so updates and reads remain local.

Example shape:

```sql
CREATE TABLE <SCHEMA>.<ROLLUP_TABLE> (
  <DIST_COLUMN> bigint NOT NULL,
  bucket_start timestamptz NOT NULL,
  metric_key text NOT NULL,
  event_count bigint NOT NULL,
  metric_sum numeric NOT NULL,
  refreshed_at timestamptz NOT NULL,
  PRIMARY KEY (<DIST_COLUMN>, bucket_start, metric_key)
);
```

Distribute and colocate the rollup deliberately. Do not rely on default colocation when the source and rollup have different operational lifecycles.

## 6. ORDER BY, LIMIT, and top-N

A global `ORDER BY ... LIMIT` can require each shard to return candidates and the receiving node to merge them. The query may still be efficient if each task can apply a local limit and the candidate set remains bounded.

Review:

- whether the query is tenant-scoped;
- whether ordering is supported by an index on each shard;
- whether ordering is deterministic;
- whether the limit can be pushed down exactly;
- rows returned per task;
- approximation settings, if any;
- pagination stability under concurrent writes.

Prefer keyset pagination over large `OFFSET` values:

```sql
SELECT <COLUMNS>
FROM <SCHEMA>.<TABLE>
WHERE <DIST_COLUMN> = <TENANT_VALUE>
  AND (<SORT_COLUMN>, <TIE_BREAKER>) < (<LAST_SORT_VALUE>, <LAST_ID>)
ORDER BY <SORT_COLUMN> DESC, <TIE_BREAKER> DESC
LIMIT <PAGE_SIZE>;
```

Include the distribution key in every page request and use a unique tie-breaker.

## 7. Window functions

Window functions can be efficient when each partition of the window is local to one shard and ordering is supported by worker indexes.

Favorable pattern:

```sql
SELECT <DIST_COLUMN>,
       <EVENT_TIME>,
       row_number() OVER (
         PARTITION BY <DIST_COLUMN>
         ORDER BY <EVENT_TIME> DESC, <ID> DESC
       ) AS rn
FROM <SCHEMA>.<TABLE>
WHERE <DIST_COLUMN> = <TENANT_VALUE>;
```

Risk increases when the window partition spans shards or requires a global order. Validate the exact plan and intermediate result size.

## 8. Join strategy matrix

| Join shape | Preferred treatment |
|---|---|
| Large distributed tables, same distribution key and colocation | Join on the distribution key and business key; expect worker-local execution |
| Distributed fact plus small shared dimension | Use a reference table when size and write rate justify replication |
| Distributed plus coordinator-local table | Consider managed local/reference conversion, query rewrite, or bounded pull-to-coordinator behavior |
| Different distribution keys | Redesign locality, use a bounded repartition join, or precompute a serving table |
| Small bounded subquery plus distributed table | Confirm push-pull behavior and intermediate-result limits |
| Outer join | Capability-test the exact outer/inner sides, recurrence, and join predicate |

Never conclude that a join is colocated solely because the distribution-column types match. Confirm the colocation ID, shard count, placement alignment, and join predicate.

## 9. Repartition and push-pull joins

A repartition join redistributes rows by the join key at execution time. It can be valid for bounded analytical work but is a warning for high-concurrency paths.

Collect:

- input rows and bytes per side;
- filter selectivity before repartition;
- repartition key skew;
- temporary/intermediate-result storage;
- network volume;
- task count and connection fan-out;
- receiving-node and worker memory;
- concurrency with other repartition queries.

A push-pull plan materializes a recurring or intermediate relation so tasks can use it. Guard it with the installed intermediate-result-size limit and measure the actual result size.

Prefer a permanent data-model fix when the same large cross-shard join is repeatedly executed.

## 10. Outer joins

Outer-join support and pushdown have evolved. Treat the exact shape as version-sensitive.

Check:

- which side is outer;
- whether one side is a reference or recurring relation;
- whether distributed tables are colocated;
- whether the predicate includes the distribution key;
- null-producing semantics after any rewrite;
- whether a planner GUC controls the optimization;
- plan changes across Citus upgrades.

Never turn an outer join into an inner join merely to make it distributable unless the business semantics prove the null-producing rows are impossible or irrelevant.

## 11. Subqueries and CTEs

Subqueries and CTEs can be:

- pushed down to workers;
- executed once as a recurring relation;
- repartitioned;
- materialized on the receiving node;
- rejected for the exact shape/version.

Use `EXPLAIN` to determine the real path. Do not rely on the historical PostgreSQL belief that every CTE is an optimization fence; behavior depends on PostgreSQL version, materialization keywords, and Citus planning.

For correlated subqueries, correlation on the distribution key is generally the strongest locality pattern. Rewrite repeated correlated work into a join only after confirming equivalent null and duplicate semantics.

## 12. Recursive CTEs and graph-like traversal

Recursive CTE support can be limited to router/single-shard forms or other version-specific shapes. For a hot recursive workload:

1. determine whether traversal stays within one distribution value;
2. add the distribution key to every recursive term;
3. cap depth and output rows;
4. index parent/edge lookup columns with the distribution key;
5. test cycles and duplicate handling;
6. consider precomputed paths or a separate graph service for cross-tenant/global traversal.

## 13. GROUPING SETS, ROLLUP, and CUBE

These forms can generate many aggregate groups and might have query-shape restrictions. Capability-test the exact SQL.

Estimate the maximum group count before production use:

```text
estimated_groups
≈ product of relevant dimension cardinalities
  × number of grouping sets
```

Use bounded dimensions, pre-aggregation, or dedicated summary tables when result cardinality is large.

## 14. Views and materialized views

### Regular views

A view does not improve data locality by itself. Inspect the expanded plan. A convenient view can hide:

- missing distribution-key filters;
- cross-shard joins;
- repeated subqueries;
- type casts that prevent index use;
- large coordinator-side intermediates.

### Materialized views

A materialized view is a physical relation with its own refresh and placement behavior. Determine whether it is local, reference, or distributed in the installed design.

For refresh:

- identify lock behavior;
- identify whether refresh is full or incremental;
- measure temporary storage and WAL;
- preserve distribution keys in the materialized result where possible;
- define freshness and failure semantics;
- validate downstream query routing after refresh.

Do not assume `REFRESH MATERIALIZED VIEW CONCURRENTLY` is supported for every distributed/materialized layout. Test the exact combination.

## 15. MERGE

`MERGE` support depends on source and target table types, join shape, distribution alignment, and Citus version.

Before using it:

```sql
SELECT version(), citus_version();
```

Then test the exact combination on representative data, including:

- multiple source rows matching one target row;
- unmatched source rows;
- target-only rows;
- null keys;
- conflict/constraint behavior;
- multi-shard routing;
- retry after partial failure.

For critical ingestion, separate `INSERT ... ON CONFLICT`, `UPDATE`, and reconciliation phases can be easier to reason about than a complex cross-shard `MERGE`.

## 16. SELECT FOR UPDATE and advisory locking

Pessimistic row locking is safest when the query routes to one shard. For cross-shard locking, check exact support and deadlock behavior.

Use a deterministic lock order for multi-row or multi-shard operations. Monitor:

- `pg_locks`;
- `pg_stat_activity`;
- `citus_lock_waits` when available;
- distributed deadlock logs/settings;
- transaction age.

Application-level PostgreSQL advisory locks are not automatically a global distributed-lock service. Define the node on which the lock is acquired and prove that all contenders use the same lock namespace and endpoint.

## 17. Sequences and identifiers

Citus can encode node information into sequence-generated values in some query-from-any-node designs. Exact behavior is version- and topology-sensitive.

For identifiers, choose deliberately among:

- application-generated UUID/ULID-like values;
- bigint identity/sequence routed through a receiving node;
- composite tenant-scoped keys;
- node-aware sequence behavior supported by the installed version;
- an external ID service.

Check:

- global versus tenant-scoped uniqueness;
- index locality and width;
- monotonicity requirements;
- offline generation;
- collision/retry behavior;
- restore and clone behavior;
- ORM assumptions;
- integer width.

Avoid narrow integer sequence types in designs that may generate IDs on multiple nodes.

## 18. Triggers

Treat triggers on distributed tables as a high-risk, version-sensitive area. The current official Citus 14 trigger guide documents manual creation on shard placements rather than ordinary coordinator-managed trigger propagation and warns that new placements created by later rebalancing might not inherit those manually installed triggers. Prefer a supported declarative or application pattern whenever possible, and verify the exact release before using a workaround.

Classify each trigger by purpose and execution location:

- row validation;
- denormalization;
- audit/event emission;
- cross-table write;
- external side effect;
- partition management.

Review:

1. whether the trigger definition propagates to shards;
2. whether referenced objects exist on workers;
3. whether the trigger performs cross-shard SQL;
4. whether it is idempotent under retry;
5. whether external effects can duplicate;
6. how it behaves during `COPY`, backfill, rebalance, and logical replication;
7. whether transition tables or statement-level behavior are supported for the layout.

Prefer transactional outbox patterns over direct external network calls from triggers.

## 19. Row-level security

RLS can provide an additional tenant-isolation layer when supported by the installed version and query path.

A tenant policy should normally bind the authenticated identity to the distribution key. Validate:

- role mapping and role inheritance;
- policy propagation;
- router and multi-shard query behavior;
- `BYPASSRLS`, table ownership, and superuser exceptions;
- prepared statements and connection-pool role reset;
- `FORCE ROW LEVEL SECURITY` requirements;
- reference/local table exposure;
- query plans and indexes for the policy predicate.

RLS does not replace application authorization, secure role provisioning, or independent tenant-isolation testing.

## 20. Logical decoding, CDC, and external integrations

Logical decoding and change-data-capture pipelines must understand that a logical distributed table is represented by physical shard relations and that shard movement can generate additional WAL activity. Current Citus releases may expose a CDC-normalization GUC, but availability and behavior are version-sensitive.

Discover rather than assume:

```sql
SELECT name, setting, context, source
FROM pg_settings
WHERE name IN (
  'wal_level',
  'max_replication_slots',
  'max_wal_senders',
  'citus.enable_change_data_capture'
)
ORDER BY name;
```

A CDC design must define:

- publication and replication-slot ownership;
- which node or nodes produce the stream;
- how physical shard names map to logical table names;
- how shard creation, movement, split, rebalance, and failover are represented or filtered;
- replica identity for updates and deletes;
- ordering guarantees within and across shards;
- duplicate handling and consumer idempotency;
- schema-change compatibility;
- WAL retention and slot-lag alerts;
- resnapshot and disaster-recovery procedure;
- behavior during Citus and PostgreSQL upgrades.

Do not deploy a generic PostgreSQL CDC connector against a Citus cluster without testing rebalance, drain, failover, and schema-change scenarios. A stream that works for steady inserts can still duplicate, omit, or mislabel events during topology changes.

For Kafka, Spark, BI, FDW, or ETL integrations, identify whether the tool connects to the logical receiving endpoint or directly to workers. Direct worker access exposes shard implementation details and should be treated as an explicit operational contract, not a transparent shortcut.

## 21. Extensions and distributed objects

An extension used by distributed queries may need binaries and compatible versions on every node that executes its functions or stores its types.

Inventory:

```sql
SELECT extname, extversion, extnamespace::regnamespace AS extension_schema
FROM pg_extension
ORDER BY extname;
```

For each extension, record:

- package/binary availability on every node;
- extension version compatibility;
- whether `CREATE EXTENSION` propagates in this topology;
- custom types, casts, operators, operator classes, collations, and aggregates;
- volatility/parallel-safety markings;
- backup/restore and upgrade procedure;
- managed-service restrictions;
- whether functions execute on workers or only on the receiving node.

Use `citus.pg_dist_object` or official distributed-object helpers only after inspecting the installed metadata and documentation. Do not edit distributed-object metadata directly.

## 22. Custom functions and distributed functions

A custom SQL/PLpgSQL function must be reviewed for:

- volatility (`IMMUTABLE`, `STABLE`, `VOLATILE`);
- parallel safety;
- security definer/search path safety;
- objects referenced on workers;
- distribution argument;
- colocation target;
- transaction and retry behavior;
- temporary tables and session state;
- dynamic SQL and identifier quoting.

For tenant-scoped multi-statement logic, a distributed function can route execution by an argument when supported:

```sql
SELECT create_distributed_function(
  '<SCHEMA>.<FUNCTION_SIGNATURE>',
  distribution_arg_name := '<DIST_ARGUMENT>',
  colocate_with := '<SCHEMA>.<COLOCATED_TABLE>'
);
```

Discover the actual signature before use. Validate that all statements inside the function remain local to the routed shard.

## 23. Custom types, enums, collations, and casts

Distributed tables can depend on objects beyond tables and functions. Before schema deployment, inventory:

- domains;
- enum types;
- composite types;
- collations;
- casts;
- operator classes;
- text-search configurations;
- generated-column expressions;
- default expressions.

The same object identity and semantics must exist wherever shard DDL or worker execution requires them. Version differences in collation libraries can also affect ordering and index validity.

## 24. JSONB and semi-structured data

JSONB can reduce schema explosion for tenant-specific attributes, but it does not eliminate data-model decisions.

Use:

- typed columns for frequently filtered/joined invariants;
- JSONB for genuinely variable attributes;
- expression or GIN indexes only for measured predicates;
- generated columns when a JSON path becomes a stable hot field;
- distribution by a stable relational tenant/entity key, not by an arbitrary JSON path.

Monitor index size, write amplification, selectivity, and tenant skew. Avoid a universal GIN index that indexes large unused documents without evidence.

## 25. Faceted search

Faceted search can combine filters, counts, and flexible attributes. Design for bounded intermediate results:

- push tenant/entity and time filters first;
- index high-selectivity relational predicates;
- use JSONB expression/GIN indexes for proven paths;
- precompute high-traffic facet counts when exact freshness is unnecessary;
- constrain the number of facets and returned values;
- benchmark skewed tenants and common empty-result cases.

For global faceting across all tenants, expect a multi-shard analytical path and isolate its resource budget.

## 26. Parallel index creation and DDL

DDL on a distributed logical table can propagate to many shards and partitions. The physical work can be much larger than the coordinator command suggests.

Estimate:

```text
physical_index_builds
≈ distributed_shards
  × leaf_partitions
  × active_placements
```

Before creating or rebuilding an index:

- count affected shard relations;
- estimate peak temporary and final disk;
- identify lock behavior;
- verify whether concurrent variants are supported for the exact layout;
- cap background concurrency;
- monitor worker CPU, I/O, WAL, locks, and invalid indexes;
- define cleanup for partial failure.

## 27. Query propagation helpers

Functions that run arbitrary SQL on workers or placements are diagnostic and repair tools, not a normal application API.

Before use:

1. prove the coordinator cannot express the operation safely;
2. bound the exact workers/shards/placements;
3. make the SQL idempotent or supply a recovery plan;
4. quote identifiers and values safely;
5. account for partial success;
6. validate every target afterward;
7. never include secrets in propagated SQL or output.

Read `references/11-command-reference.md` for risk labels and templates.

## 28. SQL compatibility test matrix

For a version-sensitive feature, test at least:

| Dimension | Values to cover |
|---|---|
| Table type | local, managed local, reference, distributed, schema, partitioned, columnar as relevant |
| Query routing | one distribution value, multiple values, no distribution filter |
| Join layout | colocated, reference, non-colocated |
| Transaction | autocommit, explicit single-shard, explicit multi-shard |
| Data shape | empty, one row, duplicates, nulls, skew, large intermediate result |
| Concurrency | one session and representative concurrent load |
| Failure | cancellation, timeout, worker loss where safe to simulate |
| Upgrade | current version and target version in staging |

Record SQLSTATE, plan, output checksum, rows affected, latency, connections, temporary bytes, and worker/coordinator resource use.

## 29. Advanced SQL review checklist

- [ ] Exact PostgreSQL and Citus versions are known.
- [ ] Table types, distribution columns, partition keys, and colocation IDs are known.
- [ ] The exact feature/function/GUC exists in the installed database.
- [ ] Query path is classified from `EXPLAIN`, not assumed.
- [ ] Null, duplicate, ordering, and retry semantics are tested.
- [ ] Intermediate rows/bytes and connection fan-out are measured.
- [ ] Approximation has an explicit accuracy contract.
- [ ] Extension objects and versions exist on every required node.
- [ ] Triggers/functions have safe volatility, propagation, and side-effect behavior.
- [ ] RLS and role-reset behavior are tested through the actual pooler.
- [ ] DDL physical fan-out and lock/storage cost are estimated.
- [ ] Unsupported shapes have a documented workaround or architecture change.
- [ ] Upgrade tests cover plan and semantic changes.
