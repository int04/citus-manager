# Citus Performance Experiment: <Hypothesis>

## 1. Hypothesis

Example format:

> Adding the distribution-key predicate and a colocated index will reduce task count from 32 to 1 and lower p95 latency below 40 ms without increasing write p95 by more than 5%.

## 2. Scope

- Query/endpoint:
- Tables/colocation groups:
- Environment:
- PostgreSQL/Citus versions:
- Dataset size/skew:
- Worker topology:
- Test window:

## 3. Controlled variables

- Data snapshot:
- Cache warm/cold state:
- Concurrency:
- Connection pool:
- Client/network:
- GUCs:
- Background jobs:
- Statistics state:

## 4. Baseline

### Query and plan

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
<BASELINE_QUERY>;
```

### Metrics

| Metric | Baseline |
|---|---:|
| Calls/sec | |
| p50 | |
| p95 | |
| p99 | |
| Task/shard count | |
| Planning time | |
| Execution time | |
| Rows returned | |
| Shared blocks read/hit | |
| Temp bytes | |
| Coordinator CPU | |
| Worker CPU max/skew | |
| Disk latency | |
| Network bytes | |
| Connections/waits | |

## 5. Proposed single change

- Change:
- Reason:
- Risk:
- Expected mechanism:
- Rollback:

## 6. Test procedure

1.
2.
3.

## 7. Result

### Query and plan

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
<CHANGED_QUERY>;
```

### Metrics

| Metric | Baseline | Changed | Difference |
|---|---:|---:|---:|
| Calls/sec | | | |
| p50 | | | |
| p95 | | | |
| p99 | | | |
| Task/shard count | | | |
| Planning time | | | |
| Execution time | | | |
| Temp bytes | | | |
| Coordinator CPU | | | |
| Worker CPU max/skew | | | |
| Connections/waits | | | |

## 8. Side effects

- Write latency:
- Storage/index size:
- Vacuum/analyze:
- Connection use:
- Coordinator pressure:
- Other query regressions:
- Rebalance/partition implications:

## 9. Decision

- Accept / Reject / Retest
- Evidence:
- Production rollout scope:
- Observation window:
- Revert threshold:

## 10. Reproducibility

- Commands/scripts:
- Data generator/snapshot:
- Commit/config revision:
- Raw result location:
