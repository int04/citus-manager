# ADR: <Decision title>

- Status: Proposed / Accepted / Rejected / Superseded
- Date:
- Owners:
- Related ADRs:
- PostgreSQL version:
- Citus version:

## Context

Describe the measured problem, workload, scale, constraints, and why a decision is required now.

## Facts

-

## Assumptions and unknowns

-

## Decision drivers

- Data locality:
- Transaction locality:
- Query latency:
- Throughput:
- Growth:
- Integrity:
- Operational complexity:
- RPO/RTO:
- Cost:

## Options considered

### Option A — <Name>

- Description:
- Benefits:
- Costs:
- Risks:
- Evidence/benchmark:

### Option B — <Name>

- Description:
- Benefits:
- Costs:
- Risks:
- Evidence/benchmark:

### Option C — <Name>

- Description:
- Benefits:
- Costs:
- Risks:
- Evidence/benchmark:

## Decision

State the chosen option precisely.

### Distribution model

- Sharding model:
- Distribution key:
- Colocation groups:
- Table types:
- Shard count:

### Partition model

- Parent tables:
- Partition key/method:
- Interval:
- Relation-count projection:
- Retention/lifecycle:

### Integrity model

- Primary/unique keys:
- Foreign keys:
- Replica identity:
- Identifier/idempotency strategy:

### Query and transaction implications

- Single-shard paths:
- Multi-shard paths:
- Cross-shard exceptions:
- Coordinator-heavy paths:

### Operational implications

- Rebalance/movement:
- HA/backup/restore:
- Monitoring:
- Upgrade/version constraints:

## Consequences

### Positive

-

### Negative

-

### Neutral or deferred

-

## Capacity calculations

```text
Current/future workers and cores:
Shard count:
Average/largest shard:
Peak task/connection demand:
Partition count:
Shard × partition × index estimate:
Storage/WAL/movement estimate:
```

## Validation

- Staging experiment:
- Success criteria:
- Failure criteria:
- Production observation window:

## Rollback or replacement strategy

Describe what can be reversed, what requires forward recovery, and the last safe rollback point.

## Revisit triggers

- Data exceeds:
- Worker count exceeds:
- Largest tenant/entity exceeds:
- p95/p99 exceeds:
- Cross-shard query percentage exceeds:
- Version/provider capability changes:
