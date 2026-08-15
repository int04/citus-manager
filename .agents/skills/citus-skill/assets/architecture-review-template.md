# Citus Architecture Review

## 1. Review metadata

- System:
- Repository/service:
- Review date:
- Reviewers:
- PostgreSQL version:
- Citus version:
- Deployment type:
- Current node topology:
- Target horizon:
- Decision deadline:

## 2. Executive decision

- Recommended architecture:
- Why Citus is or is not required:
- Primary distribution domain:
- Sharding model:
- Partitioning model:
- Storage model:
- Main risks:
- Approval status:

## 3. Measured constraints

| Constraint | Current evidence | Target | Deadline |
|---|---|---|---|
| Data size/growth | | | |
| Read throughput | | | |
| Write/ingest throughput | | | |
| p95/p99 latency | | | |
| CPU | | | |
| Memory/working set | | | |
| Disk/IOPS | | | |
| Connections | | | |
| Retention | | | |
| Tenant/entity isolation | | | |
| RPO/RTO | | | |

## 4. Alternatives considered

| Option | Benefits | Costs/risks | Evidence | Decision |
|---|---|---|---|---|
| Plain PostgreSQL | | | | |
| PostgreSQL partitioning | | | | |
| Single-node Citus | | | | |
| Row-based Citus | | | | |
| Schema-based Citus | | | | |
| Service/database separation | | | | |

## 5. Workload inventory

### Hot queries

| Query/endpoint | Calls/sec | p95 | Filters | Joins | Transaction scope | Current path | Target path |
|---|---:|---:|---|---|---|---|---|
| | | | | | | | |

### Writes and transactions

| Operation | Rows/sec | Tables | Candidate locality key | Single/multi-shard | Atomicity |
|---|---:|---|---|---|---|
| | | | | | |

### Cross-domain operations

| Operation | Frequency | Data volume | Latency need | Proposed handling |
|---|---:|---:|---|---|
| | | | | |

## 6. Table classification

| Table | Rows/size | Growth | Hot access | Proposed type | Distribution key | Colocation root | Partition key | Access method | Rationale |
|---|---:|---:|---|---|---|---|---|---|---|
| | | | | | | | | | |

## 7. Distribution-key scorecard

| Candidate | Hot filters | Hot joins | Transaction locality | Cardinality | Evenness | Immutable | Propagates through schema | Integrity fit | Total | Disqualifiers |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| | | | | | | | | | | |

## 8. Colocation groups

| Group | Root table | Member tables | Shared transaction | Shared join | Shard count | Movement coupling | Largest tenant/entity |
|---|---|---|---|---|---:|---|---|
| | | | | | | | |

## 9. Integrity model

- Primary keys:
- Unique constraints:
- Foreign keys:
- Replica identity:
- Identifier generation:
- Idempotency strategy:
- Cross-shard/global uniqueness:
- Late correction behavior:

## 10. Shard and capacity plan

### Inputs

- Current workers/cores:
- Future workers/cores:
- Worker `max_connections`:
- Reserved connection headroom:
- Peak concurrent distributed queries:
- Target shard size:
- Largest colocation group:
- Largest tenant/entity:

### Calculations

```text
Available worker connections =
Peak distributed task demand =
Chosen shard count =
Projected average shard bytes =
Projected largest shard bytes =
Estimated movement time =
```

### Decision

- Shard count by colocation group:
- Reason:
- Scale-out limit before resharding:
- Connection safeguards:

## 11. Partition plan

- Parent tables:
- Partition method/key:
- Interval:
- Retention:
- Hot window:
- Future creation horizon:
- Default partition policy:
- Late-arrival percentile:
- Archive/drop policy:
- Automation owner:

```text
shard_count × active_partitions × indexes =
projected physical relation/placement impact =
```

## 12. Storage plan

| Data tier | Age/range | Heap/columnar | Mutable? | Indexes | Backup/archive | Conversion criteria |
|---|---|---|---|---|---|---|
| Hot | | | | | | |
| Warm | | | | | | |
| Cold | | | | | | |
| Expired | | | | | | |

## 13. Query-path review

| Query | Distribution predicate | Partition predicate | Colocated? | Task count | Intermediate data | Coordinator work | Required change |
|---|---|---|---|---:|---:|---|---|
| | | | | | | | |

## 14. Operations and reliability

- Add/rebalance plan:
- Drain/remove plan:
- Hot-tenant isolation:
- Coordinator HA:
- Worker HA:
- Backup/PITR:
- Restore test:
- Upgrade path:
- Security/TLS:
- Observability/alerts:

## 15. Migration plan summary

- Schema phases:
- Key backfill:
- Data movement:
- Change synchronization:
- Cutover:
- Validation:
- Rollback window:
- Cleanup:

## 16. Risks

| Risk | Probability | Impact | Detection | Mitigation | Owner |
|---|---|---|---|---|---|
| | | | | | |

## 17. Acceptance criteria

- [ ]
- [ ]
- [ ]

## 18. Open questions

- [ ]
- [ ]
