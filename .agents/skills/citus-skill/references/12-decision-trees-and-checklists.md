# Citus Decision Trees and Checklists

Use these compact gates after reading the deeper references. A checklist does not replace evidence or version detection.

## 1. Should this system use Citus?

```text
Is one PostgreSQL node currently or predictably unable to meet storage,
CPU, write throughput, isolation, or operational scale requirements?
├─ No → Fix schema, queries, indexes, vacuum, pooling, and hardware first.
└─ Yes
   ├─ Can most hot data and transactions be grouped by a stable key?
   │  ├─ Yes → Evaluate row-based Citus.
   │  └─ No
   │     ├─ Are workloads naturally isolated by tenant/service schema?
   │     │  ├─ Yes → Evaluate schema-based Citus.
   │     │  └─ No → Consider service/database separation or redesign.
   └─ Is the only need time retention/pruning on one server?
      ├─ Yes → PostgreSQL partitioning may be sufficient.
      └─ No → Continue Citus architecture review.
```

Approval gate:

- [ ] The bottleneck is measured.
- [ ] Vertical scaling and plain partitioning were compared.
- [ ] A locality domain exists.
- [ ] Distributed operational complexity is accepted.
- [ ] Backup, HA, and observability ownership exists.

## 2. Row-based or schema-based sharding?

```text
Do many tenants/entities share one uniform schema?
├─ Yes
│  ├─ Can queries and transactions carry tenant/entity ID?
│  │  ├─ Yes → Prefer row-based sharding.
│  │  └─ No → Estimate application change versus schema model.
│  └─ Is tenant density very high?
│     └─ Yes → Row-based usually fits better.
└─ No
   ├─ Is each tenant/service already isolated in its own schema?
   │  ├─ Yes → Evaluate schema-based sharding.
   │  └─ No → Row-based or service separation.
   ├─ Are cross-schema joins/foreign keys common?
   │  └─ Yes → Schema-based may be a poor fit.
   └─ Can one schema become too large/hot to place as one unit?
      └─ Yes → Row-based or a finer service boundary may be safer.
```

## 3. Table-type decision

```text
Is the table large or expected to scale horizontally?
├─ Yes
│  ├─ Does it have a strong routing key shared with related tables?
│  │  ├─ Yes → Distributed table, deliberate colocation.
│  │  └─ No → Independent distributed group, schema model, or redesign.
│  └─ Is it immutable analytical history?
│     └─ Evaluate columnar or hybrid partition storage.
└─ No
   ├─ Is it a small shared lookup joined by distributed tables?
   │  ├─ Yes → Reference table.
   │  └─ No
   ├─ Must it be accessible from metadata/query nodes?
   │  ├─ Yes → Managed local when supported.
   │  └─ No → Local table.
   └─ Is it tenant/service scoped inside a distributed schema?
      └─ Schema table.
```

Reject reference-table choice when:

- table size is large or unbounded;
- writes are frequent/heavy;
- replication to every worker is too expensive;
- different tenants need different rows rather than one shared copy.

## 4. Distribution-key scorecard

Score 0–5 and document evidence:

| Criterion | Weight suggestion | Candidate score |
|---|---:|---:|
| Present in hot filters | 5 | |
| Present in hot joins | 5 | |
| Defines transaction boundary | 5 | |
| High cardinality | 4 | |
| Even distribution | 5 | |
| Immutable | 5 | |
| Available on all related tables | 4 | |
| Supports integrity constraints | 4 | |
| Supports hot-tenant isolation | 3 | |
| Fits future architecture | 4 | |

Disqualify or heavily penalize:

- nullable key;
- mutable ownership key;
- timestamp used only for ranges;
- boolean/status/category with few values;
- one dominant value;
- key absent from application/API context;
- key that breaks the primary transaction domain.

## 5. Colocation decision

Colocate tables only when all are true:

- [ ] Same semantic distribution domain.
- [ ] Compatible distribution-column types.
- [ ] Hot joins use that key.
- [ ] Transactions update them together.
- [ ] Shared shard count is acceptable.
- [ ] They can move/rebalance together.
- [ ] Replica identity supports movement.

Use `colocate_with => 'none'` when tables are operationally independent even if column types match.

## 6. Shard-count decision

Inputs:

- current and future workers;
- current and future worker cores;
- data size and growth per colocation group;
- largest tenant/entity;
- single-shard versus multi-shard workload ratio;
- peak concurrent distributed queries;
- worker `max_connections` and reserved headroom;
- target shard size and movement duration;
- partition count and indexes;
- planning/metadata overhead tolerance.

Checks:

```text
Is shard count lower than future workers?
├─ Yes → Some workers cannot hold a primary shard for that group.
└─ No → Continue.

Does peak concurrent multi-shard work × tasks per query exceed
available worker connection capacity?
├─ Yes → Reduce tasks/shards, concurrency, or increase safe capacity.
└─ No → Benchmark.

Does shard_count × partitions × indexes create excessive relations?
├─ Yes → Reduce shard or partition granularity.
└─ No → Benchmark projected scale.
```

Starting ranges from official Citus guidance are benchmark seeds, not universal defaults. Record why the chosen number fits this workload.

## 7. Does this table need PostgreSQL partitioning?

```text
Do most queries touch a small subset of a domain such as recent time?
├─ No
│  ├─ Is retention/drop/archive by range required?
│  │  ├─ Yes → Partition may still be justified.
│  │  └─ No → Avoid partitioning unless another measured benefit exists.
└─ Yes
   ├─ Can predicates support partition pruning?
   │  ├─ Yes → Evaluate range/list partitioning.
   │  └─ No → Query redesign may be required first.
   └─ Is lifecycle automation owned and monitored?
      ├─ No → Do not deploy partitions yet.
      └─ Yes → Calculate relation count and lock behavior.
```

## 8. Partition interval decision

For each candidate interval:

- [ ] Bytes and rows per partition are known.
- [ ] Typical query scans few partitions.
- [ ] Retention/archival unit is useful.
- [ ] Active indexes fit the memory/maintenance target.
- [ ] `shards × partitions × indexes` is acceptable.
- [ ] Planning time tested at projected count.
- [ ] Create/attach/detach/drop locks tested.
- [ ] Future partitions are pre-created.
- [ ] Late data policy is compatible.

Choose the coarsest interval that still satisfies pruning and lifecycle goals, unless benchmarks prove finer intervals beneficial.

## 9. Heap, columnar, or hybrid?

```text
Does the data require normal UPDATE/DELETE, row locks, selective indexes,
or broad constraint support?
├─ Yes → Heap.
└─ No
   ├─ Is the workload dominated by large scans/aggregates and compression?
   │  ├─ Yes → Evaluate columnar.
   │  └─ No → Heap is simpler.
   └─ Is only old history immutable?
      └─ Use hybrid partitioning: hot heap, cold columnar.
```

Columnar gate:

- [ ] Exact installed limitations tested.
- [ ] Index/constraint loss understood.
- [ ] CDC/logical decoding compatible.
- [ ] Bulk-write pattern produces useful stripes.
- [ ] Reverse conversion tested.
- [ ] Late corrections have a process.

## 10. Query-path decision

```text
Does the query have an equality predicate on the distribution key?
├─ Yes → Check single-shard routing.
└─ No
   ├─ Are large distributed tables colocated and joined on the key?
   │  ├─ Yes → Colocated multi-shard path.
   │  └─ No
   ├─ Is the other table a suitable reference table?
   │  ├─ Yes → Reference join.
   │  └─ No
   ├─ Can partial aggregation/filtering reduce data on workers?
   │  ├─ Yes → Bounded multi-shard path.
   │  └─ No → Repartition/coordinator-heavy risk.
   └─ Is this a hot latency-sensitive path?
      ├─ Yes → Redesign data/query/read model.
      └─ No → Benchmark and constrain resources.
```

## 11. Performance triage order

1. Verify query and parameters.
2. Verify distribution/partition pruning.
3. Verify colocation and pushdown.
4. Verify worker indexes and statistics.
5. Verify tenant/shard/partition/node skew.
6. Verify connection fan-out and waits.
7. Verify coordinator intermediate results and temp I/O.
8. Verify vacuum/bloat and disk latency.
9. Test one change at a time.
10. Change GUCs or hardware only after model/query evidence.

## 12. Add-worker gate

- [ ] Same compatible PostgreSQL/Citus line.
- [ ] Correct database, roles, extensions, TLS, and authentication.
- [ ] Network reachability in required directions.
- [ ] Node capacity and monitoring ready.
- [ ] Coordinator host/metadata configuration verified.
- [ ] Reference tables and metadata synchronize.
- [ ] Rebalance plan previewed.
- [ ] WAL, slot, disk, network, and connection capacity available.
- [ ] New worker receiving zero shards before rebalance is understood as normal.
- [ ] Post-rebalance placement and query validation defined.

## 13. Drain/remove gate

```text
Is target worker reachable and healthy enough to transfer data?
├─ No → Use HA/restore/failure runbook; do not pretend drain can recover lost shards.
└─ Yes
   ├─ Do remaining workers have capacity?
   │  ├─ No → Add capacity first.
   │  └─ Yes → Preview and drain.
   └─ Are placements on target exactly zero?
      ├─ No → Do not remove or delete infrastructure.
      └─ Yes → Remove metadata, then decommission.
```

## 14. Change shard count/distribution/colocation gate

- [ ] Installed `alter_distributed_table` signature supports the change.
- [ ] Every affected colocated table is listed.
- [ ] Replica identity is valid.
- [ ] Disk/WAL/network/connection capacity is sufficient.
- [ ] Partition/relation-count impact calculated.
- [ ] Application compatibility tested.
- [ ] Staging duration and locks measured.
- [ ] Backup and recovery path tested.
- [ ] Validation and abort thresholds defined.
- [ ] Shadow-table migration considered as an alternative.

## 15. Incident triage tree

```text
Preserve exact error, SQLSTATE, SQL, parameters, timestamp, and node.
|
+-- Is the extension/function/view missing?
|   +-- Check current database, pg_extension, pg_proc, version, managed limits.
|
+-- Is it connectivity/authentication?
|   +-- Check node metadata, DNS/port/TLS/HBA/role/authinfo status.
|
+-- Is it query support/routing?
|   +-- EXPLAIN, distribution key, colocation, intermediate result, SQL limits.
|
+-- Is it resource exhaustion?
|   +-- Connections, disk, WAL, slots, memory, temp, CPU, network.
|
+-- Is it lock/transaction?
|   +-- pg_stat_activity, citus_lock_waits, prepared xacts, deadlock order.
|
+-- Is it rebalance/data movement?
|   +-- job/task status, replica identity, source/target health, slots/WAL.
|
+-- Is it corruption/read error?
    +-- Stop destructive movement, preserve logs, isolate node, verify storage,
        restore/HA plan, and escalate.
```

## 16. Production design review

- [ ] Citus necessity and alternative analysis complete.
- [ ] Sharding model approved.
- [ ] Every table classified.
- [ ] Distribution key and colocation evidence documented.
- [ ] PK/UNIQUE/FK/replica identity valid.
- [ ] Shard count and future worker plan calculated.
- [ ] Partition interval and relation count calculated.
- [ ] Hot/cold storage lifecycle documented.
- [ ] Top queries classified and benchmarked.
- [ ] Connection budget and pool limits defined.
- [ ] Largest/hottest tenant/entity tested.
- [ ] Add/rebalance/drain/remove rehearsed.
- [ ] Backup/PITR/restore tested.
- [ ] HA and failover tested per node role.
- [ ] Upgrade and configuration-drift process exists.
- [ ] Security and secret handling meet policy.
- [ ] Alerts cover node, shard, partition, query, WAL, slot, lock, and backup health.

## 17. Change-ticket minimum

Every high-impact ticket must state:

```text
Purpose:
Scope:
Confirmed versions/capabilities:
Affected tables/colocation groups/nodes:
Data volume and projected movement:
Lock and capacity analysis:
Read-only preflight:
Execution phases:
Checkpoint after each phase:
Metrics and thresholds:
Abort conditions:
Validation:
Rollback or forward recovery:
Backup/restore evidence:
Owners and communication:
Cleanup criteria:
```

## 18. Architecture decision minimum

Every approved Citus design decision should record:

- context and measured constraint;
- options considered;
- decision and rationale;
- distribution and partition dimensions;
- integrity model;
- query/transaction implications;
- capacity calculations;
- operational consequences;
- risks and mitigations;
- validation plan;
- revisit triggers.

Use `assets/design-decision-record-template.md`.
