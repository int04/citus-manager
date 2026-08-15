# Citus Migration Runbook: <Change>

## 1. Change metadata

- Change ID:
- Date/window:
- Owners:
- Approvers:
- Environment:
- Database:
- PostgreSQL/Citus versions:
- Risk class:
- Communication channel:

## 2. Purpose

State exactly what changes and why.

## 3. Scope

### Included

- Tables/schemas:
- Colocation groups:
- Nodes:
- Partitions/ranges:
- Application services:

### Excluded

-

## 4. Confirmed facts

-

## 5. Assumptions/unknowns

-

## 6. Success criteria

| Metric/check | Baseline | Required result | Owner |
|---|---:|---:|---|
| | | | |

## 7. Abort criteria

| Signal | Threshold | Action | Owner |
|---|---|---|---|
| Error rate | | | |
| p95/p99 latency | | | |
| Worker connections | | | |
| Disk/WAL | | | |
| Replication lag/slots | | | |
| Lock duration | | | |
| Job/task failures | | | |

## 8. Backup and recovery evidence

- Latest recoverable backup:
- Restore-test date/result:
- PITR/restore point:
- Rollback owner:
- Last safe rollback point:
- Forward-recovery alternative:

## 9. Capacity and lock analysis

- Data bytes/rows moved:
- Source free space:
- Target free space:
- WAL estimate/headroom:
- Network estimate:
- Connection estimate:
- Expected locks:
- Expected duration:
- Conflicting jobs disabled/rescheduled:

## 10. Read-only preflight

```sql
-- Version/capability checks
```

```sql
-- Table/shard/partition/node inventory
```

```sql
-- Constraints/replica identity
```

```sql
-- Connections/locks/slots/prepared transactions
```

Checkpoint: all preflight conditions pass.

## 11. Staging rehearsal

- Dataset representativeness:
- Commands tested:
- Duration:
- Peak resources:
- Lock observations:
- Failure injection:
- Rollback test:
- Differences from production:

## 12. Execution phases

### Phase 1 — <Name>

Purpose:

Commands:

```sql
-- Commands with placeholders replaced and node/database stated.
```

Validation:

```sql
-- Read-only validation.
```

Pass condition:

Abort/rollback:

### Phase 2 — <Name>

Purpose:

Commands:

```sql
```

Validation:

```sql
```

Pass condition:

Abort/rollback:

### Phase 3 — <Name>

Purpose:

Commands:

```sql
```

Validation:

```sql
```

Pass condition:

Abort/rollback:

## 13. Live monitoring

- Application latency/errors:
- Coordinator CPU/memory/network/temp:
- Worker CPU/disk/network:
- Connections:
- WAL/slots/replication lag:
- Rebalance/background jobs:
- Locks/prepared transactions:
- Partition jobs:

## 14. Data validation

- Counts by key/range:
- Aggregates:
- Duplicates/nulls/orphans:
- Sample comparison:
- Constraint status:
- Application shadow reads:

## 15. Post-change validation

- Metadata:
- Query routing/plans:
- Performance:
- Backup/replication:
- Application smoke tests:
- Alerts:

## 16. Cleanup

Cleanup is allowed only after success criteria and observation window pass.

- Remove old objects:
- Remove temporary sync/backfill jobs:
- Restore normal traffic/jobs:
- Update documentation/ADR:
- Retain audit evidence:

## 17. Rollback

### Before cutover

-

### During cutover

-

### After cutover

-

## 18. Timeline and record

| Time | Action | Result | Operator |
|---|---|---|---|
| | | | |
