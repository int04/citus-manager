# Citus Incident Report: <Title>

## 1. Summary

- Start time/time zone:
- End time:
- Severity:
- Customer/system impact:
- Affected database/nodes/tables:
- Detection source:
- Incident commander:

## 2. Exact error evidence

- SQLSTATE:
- Error message:
- Failing SQL and redacted parameters:
- Node that emitted the error:
- PostgreSQL/Citus versions:
- Relevant log references:

Do not include passwords, `pg_dist_authinfo.authinfo`, private keys, or unredacted customer data.

## 3. Timeline

| Time | Event/action | Evidence/result | Owner |
|---|---|---|---|
| | | | |

## 4. Impact

- Failed requests/transactions:
- Latency degradation:
- Data loss/corruption status:
- Affected tenants/entities:
- RPO/RTO effect:
- Backlog/recovery work:

## 5. System state

### Topology and metadata

```sql
-- pg_dist_node, citus_tables, citus_shards, job status
```

### Connections and locks

```sql
-- pg_stat_activity, citus_lock_waits
```

### WAL/replication/transactions

```sql
-- pg_replication_slots, pg_prepared_xacts
```

### Resources

- Coordinator CPU/memory/disk/network:
- Worker CPU/memory/disk/network:
- Free disk/WAL:
- Connection utilization:

## 6. Recent changes

- Deployment:
- Migration/DDL:
- Rebalance/drain:
- Partition lifecycle:
- Version/configuration:
- Certificate/network:
- Backup/restore:

## 7. Root-cause analysis

### Proven cause

- Evidence:

### Contributing factors

-

### Rejected hypotheses

| Hypothesis | Evidence against it |
|---|---|
| | |

## 8. Mitigation and recovery

- Immediate mitigation:
- Data repair/replay:
- Prepared transaction handling:
- Worker/node recovery:
- Validation:
- Customer communication:

## 9. Data-integrity verification

- Counts/aggregates:
- Duplicate/orphan/null checks:
- Shard/placement checks:
- Application reconciliation:
- Backup/restore use:

## 10. What went well

-

## 11. What went poorly

-

## 12. Corrective actions

| Action | Type | Owner | Deadline | Verification |
|---|---|---|---|---|
| | Prevent/Detect/Mitigate/Recover | | | |

## 13. Runbook/skill updates

- Reference or command guidance to update:
- New alert or diagnostic script:
- New staging/failure test:
- Version-specific note:
