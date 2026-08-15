# Citus Cluster Operations

Use `08-observability-security-ha-and-upgrades.md` for monitoring, backup, HA, and upgrade gates that surround these runbooks.

These runbooks are independent of Docker, virtual machines, Kubernetes, and managed services. Replace placeholders with the real topology. On managed platforms, some node-management functions may be blocked or replaced by the provider's control plane.

## 1. General preflight

Before any topology change or data-movement operation, collect:

```sql
SELECT version();
SELECT citus_version();

SELECT *
FROM pg_dist_node
ORDER BY nodeid;

SELECT table_name,
       citus_table_type,
       distribution_column,
       colocation_id,
       shard_count,
       table_size
FROM citus_tables
ORDER BY table_name;

SELECT nodename,
       nodeport,
       count(*) AS placements,
       pg_size_pretty(sum(shard_size)::bigint) AS bytes
FROM citus_shards
GROUP BY nodename, nodeport
ORDER BY sum(shard_size) DESC;
```

Check health and resource-related state:

```sql
SELECT *
FROM citus_check_cluster_node_health()
WHERE result = false;

SELECT * FROM pg_replication_slots;
SELECT * FROM pg_prepared_xacts;
SELECT * FROM citus_remote_connection_stats();
SELECT * FROM citus_lock_waits;
```

Outside SQL, verify:

- free disk space on source and target nodes;
- disk latency, CPU, memory, and network capacity;
- WAL retention and backup status;
- worker ports, firewalls, and DNS;
- the same database, required extensions, and roles on every relevant node;
- tested backup, PITR, and restore procedures;
- a maintenance window and a named rollback owner.

## 2. Add a worker

### Goal

Register a new worker, synchronize metadata and reference tables, and distribute existing shards to the node.

### Step 1 — validate the worker independently

From the control node or coordinator, test connectivity with an appropriate PostgreSQL client. On the worker, run:

```sql
SELECT version();
SELECT citus_version();
SELECT current_database();
SELECT current_user;
SHOW wal_level;
SHOW max_prepared_transactions;
```

The version, database, extension, and authentication setup must be compatible with the cluster.

### Step 2 — declare the coordinator host when needed

A cluster that started as single-node may still advertise the coordinator as `localhost`:

```sql
SELECT citus_set_coordinator_host(
  '<COORDINATOR_HOST>',
  <COORDINATOR_PORT>
);
```

Inspect `pg_dist_node` before adding the worker.

### Step 3A — add directly

```sql
SELECT citus_add_node(
  '<NEW_WORKER_HOST>',
  <NEW_WORKER_PORT>
);
```

### Step 3B — inactive → authentication/metadata → activate

When activation must be controlled:

```sql
SELECT citus_add_inactive_node(
  '<NEW_WORKER_HOST>',
  <NEW_WORKER_PORT>
);
```

Complete authentication through the system's approved mechanism. Do not print secrets. Then activate the node:

```sql
SELECT citus_activate_node(
  '<NEW_WORKER_HOST>',
  <NEW_WORKER_PORT>
);
```

### Checkpoint 1 — node is active

```sql
SELECT nodeid,
       nodename,
       nodeport,
       isactive,
       hasmetadata,
       metadatasynced,
       shouldhaveshards
FROM pg_dist_node
WHERE nodename = '<NEW_WORKER_HOST>'
  AND nodeport = <NEW_WORKER_PORT>;
```

### Step 4 — preview the rebalance

```sql
SELECT *
FROM get_rebalance_table_shards_plan();
```

Review total bytes, move count, source and target nodes, and affected colocation groups.

### Step 5 — rebalance

```sql
SELECT citus_rebalance_start();
```

Monitor:

```sql
SELECT * FROM citus_rebalance_status();
SELECT * FROM pg_replication_slots;
```

Wait in automation:

```sql
SELECT citus_rebalance_wait();
```

Stop the job when intervention is required:

```sql
SELECT citus_rebalance_stop();
```

### Checkpoint 2 — the worker has received placements

```sql
SELECT table_name,
       count(*) AS placements,
       pg_size_pretty(sum(shard_size)::bigint) AS bytes
FROM citus_shards
WHERE nodename = '<NEW_WORKER_HOST>'
  AND nodeport = <NEW_WORKER_PORT>
GROUP BY table_name
ORDER BY sum(shard_size) DESC;
```

### Completion criteria

- the node is active and metadata is synchronized;
- no health checks fail;
- the rebalance job completes;
- placement and disk distribution match the chosen strategy;
- query routing and latency remain healthy;
- reference tables are accessible.

## 3. Rebalance an existing cluster

### When to run it

- after adding a worker;
- when disks or nodes are imbalanced;
- after changing worker capacity;
- after isolating or moving a tenant;
- while preparing to drain a node;
- after changing shard count or layout.

### Preview

```sql
SELECT *
FROM get_rebalance_table_shards_plan();
```

### Strategy

Inspect available strategies:

```sql
SELECT *
FROM pg_dist_rebalance_strategy
ORDER BY name;
```

The default is commonly `by_disk_size`. Specify `by_shard_count` only when shard sizes, traffic, and worker capacities are genuinely similar.

### Start in the background

```sql
SELECT citus_rebalance_start();
```

Newer versions may support parallel-transfer parameters. Enable them only after a capability check and resource assessment.

### Monitor

```sql
SELECT * FROM citus_rebalance_status();
SELECT * FROM pg_dist_background_job ORDER BY job_id DESC;
SELECT * FROM pg_dist_background_task ORDER BY job_id DESC, task_id;
```

### If the operation fails

Do not restart or delete the source node immediately. Collect:

- the error message and job/task details;
- primary key or replica identity for every table in the colocation group;
- disk, WAL, and replication-slot state;
- authentication and network state;
- source and target shard state.

Fix the cause, then resume or restart according to the capabilities of the installed version.

## 4. Drain and remove one worker

### Step 1 — inventory the target node

```sql
SELECT *
FROM pg_dist_node
WHERE nodename = '<WORKER_HOST>'
  AND nodeport = <WORKER_PORT>;
```

```sql
SELECT table_name,
       count(*) AS placements,
       pg_size_pretty(sum(shard_size)::bigint) AS bytes
FROM citus_shards
WHERE nodename = '<WORKER_HOST>'
  AND nodeport = <WORKER_PORT>
GROUP BY table_name
ORDER BY sum(shard_size) DESC;
```

The remaining workers must have enough disk, IOPS, CPU, WAL, and network capacity.

### Method A — synchronously drain one node

```sql
SELECT *
FROM citus_drain_node(
  '<WORKER_HOST>',
  <WORKER_PORT>
);
```

### Method B — background drain

Mark the node as ineligible for shards:

```sql
SELECT citus_set_node_property(
  '<WORKER_HOST>',
  <WORKER_PORT>,
  'shouldhaveshards',
  false
);
```

Inspect every node currently marked `false`:

```sql
SELECT nodename, nodeport, shouldhaveshards
FROM pg_dist_node
WHERE shouldhaveshards = false;
```

Preview:

```sql
SELECT *
FROM get_rebalance_table_shards_plan(
  drain_only => true
);
```

Start:

```sql
SELECT citus_rebalance_start(
  drain_only => true
);
```

Monitor and wait as described in the rebalance runbook.

### Mandatory checkpoint — placement count equals zero

```sql
SELECT count(*) AS placements_left
FROM citus_shards
WHERE nodename = '<WORKER_HOST>'
  AND nodeport = <WORKER_PORT>;
```

If the result is not `0`, stop. Do not remove the node, stop PostgreSQL, or delete its volume or VM.

### Remove the node from metadata

```sql
SELECT citus_remove_node(
  '<WORKER_HOST>',
  <WORKER_PORT>
);
```

Verify removal:

```sql
SELECT *
FROM pg_dist_node
WHERE nodename = '<WORKER_HOST>'
  AND nodeport = <WORKER_PORT>;
```

Only after the metadata row is gone should the infrastructure be stopped or deleted.

### Cancel the drain intent

```sql
SELECT citus_set_node_property(
  '<WORKER_HOST>',
  <WORKER_PORT>,
  'shouldhaveshards',
  true
);
```

Shards already moved do not return automatically. Run a normal rebalance if redistribution is desired.

## 5. Drain multiple workers

Do not drain nodes one by one when doing so would move the same shards repeatedly.

1. Set `shouldhaveshards=false` on every target node.
2. Review the complete list of nodes marked `false`.
3. Preview with `drain_only => true`.
4. Start one background drain.
5. Verify zero placements on each node.
6. Remove each node.

```sql
SELECT citus_rebalance_start(drain_only => true);
```

## 6. Change the shard count

### When it may be needed

- the current shard count cannot use the target number of workers or CPU cores;
- shards are too large to move or recover within the required time;
- too many shards create planning and connection overhead;
- a colocation group needs a consistent new layout.

### Preflight

```sql
SELECT table_name,
       distribution_column,
       colocation_id,
       shard_count,
       table_size
FROM citus_tables
WHERE table_name = '<SCHEMA>.<TABLE>'::regclass;
```

List every table in the colocation group:

```sql
SELECT table_name,
       distribution_column,
       shard_count,
       colocation_id
FROM citus_tables
WHERE colocation_id = (
  SELECT colocation_id
  FROM citus_tables
  WHERE table_name = '<SCHEMA>.<TABLE>'::regclass
)
ORDER BY table_name;
```

Check primary keys and replica identity for every table in the group.

### Execute

Preserve colocation:

```sql
SELECT alter_distributed_table(
  '<SCHEMA>.<TABLE>',
  shard_count => <NEW_SHARD_COUNT>,
  cascade_to_colocated => true
);
```

### Validate

```sql
SELECT table_name,
       shard_count,
       colocation_id
FROM citus_tables
WHERE colocation_id = <EXPECTED_COLOCATION_ID>
ORDER BY table_name;
```

```sql
SELECT table_name,
       count(DISTINCT shardid) AS shard_count
FROM citus_shards
WHERE table_name IN (
  '<SCHEMA>.<TABLE>'::regclass
)
GROUP BY table_name;
```

Run query regression tests and compare connection and task counts.

### Rollback

The shard count can potentially be changed back with another `alter_distributed_table` call, but that is another data-movement operation, not an immediate rollback. Test in staging and maintain backup and cutover plans.

## 7. Convert a populated local table to distributed

### Step 1 — audit the schema

Confirm that:

- the distribution key exists and is non-null;
- primary and unique keys include it;
- foreign-key chains have been addressed;
- workers have enough disk space;
- queries and migrations provide the key.

### Step 2 — distribute the table

```sql
SELECT create_distributed_table(
  '<SCHEMA>.<TABLE>',
  '<DIST_COLUMN>',
  colocate_with => 'none',
  shard_count => <SHARD_COUNT>
);
```

Or colocate it with an existing root table.

### Step 3 — validate data on workers

```sql
SELECT table_name,
       count(DISTINCT shardid) AS shards,
       sum(shard_size) AS total_bytes
FROM citus_shards
WHERE table_name = '<SCHEMA>.<TABLE>'::regclass
GROUP BY table_name;
```

Validate row counts or checksums by batch/key in staging, or use an equivalent logical validation query.

### Step 4 — remove residual local rows

```sql
SELECT truncate_local_data_after_distributing_table(
  '<SCHEMA>.<TABLE>'
);
```

Run only after the checkpoint, backup validation, and review of foreign-key cascades.

## 8. Convert a distributed or reference table to local

Preflight checks:

- sufficient coordinator disk space;
- row count and total size;
- foreign-key cascade scope;
- application downtime and routing impact;
- backup coverage.

Execute:

```sql
SELECT undistribute_table('<SCHEMA>.<TABLE>');
```

Use a controlled cascade only when required. Verify that the table becomes local and that worker shard metadata is cleaned up correctly. Do not assume the operation is fast for large tables.

## 9. Change the distribution column

This is an architectural migration, not merely one SQL statement.

### Required evaluation

- every row has the new key;
- cardinality and skew are acceptable;
- queries, filters, and joins use the new key;
- primary keys, unique constraints, and foreign keys are compatible;
- colocation groups are redesigned where needed;
- application parameters are updated;
- data movement and downtime are understood;
- rollback is defined.

When supported by the installed version:

```sql
SELECT alter_distributed_table(
  '<SCHEMA>.<TABLE>',
  distribution_column => '<NEW_DIST_COLUMN>'
);
```

For critical systems, a safer pattern is often:

1. create a new schema or table with the correct layout;
2. backfill in batches;
3. dual-write or enter a maintenance window;
4. validate;
5. cut over;
6. retain the old table for a rollback period.

Choose the method from table size and downtime budget.

## 10. Split or merge colocation groups

### Separate an unrelated table

```sql
SELECT update_distributed_table_colocation(
  '<SCHEMA>.<TABLE>',
  colocate_with => 'none'
);
```

This function may update metadata only. Verify layout compatibility and the installed version documentation.

### Join another table's colocation group

```sql
SELECT update_distributed_table_colocation(
  '<SCHEMA>.<TABLE>',
  colocate_with => '<SCHEMA>.<TARGET_TABLE>'
);
```

When shard layouts differ, use `alter_distributed_table` or a migration that performs the required data movement.

### Validate

```sql
SELECT table_name, colocation_id, shard_count, distribution_column
FROM citus_tables
WHERE table_name IN (
  '<SCHEMA>.<TABLE>'::regclass,
  '<SCHEMA>.<TARGET_TABLE>'::regclass
);
```

## 11. Isolate and move a hot tenant

### Identify the tenant

Combine data size and traffic measurements. Do not rely on row count alone.

### Isolate

```sql
SELECT isolate_tenant_to_new_shard(
  '<SCHEMA>.<ROOT_TABLE>',
  <TENANT_VALUE>,
  'CASCADE'
) AS shard_id;
```

`CASCADE` affects related colocated tables. Inspect size, replica identity, and the expected movement first.

### Move

```sql
SELECT citus_move_shard_placement(
  <SHARD_ID>,
  '<SOURCE_HOST>',
  <SOURCE_PORT>,
  '<TARGET_HOST>',
  <TARGET_PORT>
);
```

Validate the tenant's placement and latency. Do not move it to an overloaded node or a node with `shouldhaveshards=false`.

## 12. Schema-based operations

### Distribute a schema

```sql
SELECT citus_schema_distribute('<SCHEMA_NAME>');
```

### Move a schema

```sql
SELECT citus_schema_move(
  '<SCHEMA_NAME>',
  '<TARGET_HOST>',
  <TARGET_PORT>
);
```

### Undistribute a schema

```sql
SELECT citus_schema_undistribute('<SCHEMA_NAME>');
```

Every operation moves data. Inspect cross-schema dependencies, total size, and application routing first.

## 13. Secondary query node or coordinator

A Citus topology may be coordinator-centric or query-from-any-node. Do not assume every node is authorized to run DDL or topology changes.

Audit:

```sql
SELECT nodeid,
       nodename,
       nodeport,
       hasmetadata,
       metadatasynced,
       isactive,
       shouldhaveshards
FROM pg_dist_node
ORDER BY nodeid;
```

For a query-only node, it is common to set:

```sql
SELECT citus_set_node_property(
  '<QUERY_NODE_HOST>',
  <QUERY_NODE_PORT>,
  'shouldhaveshards',
  false
);
```

Managed local tables require correct metadata and authentication so the query node can access coordinator-resident data.

Point migrations, DDL, and topology operations at the project's designated control endpoint.

## 14. Backup and snapshot coordination

`citus_drain_node` and replication are not backups.

A Citus backup must cover:

- coordinator/control metadata;
- every worker that stores shards;
- WAL/PITR or coordinated snapshots;
- roles, extensions, and configuration;
- a tested full-cluster restore.

When the installed version provides `citus_cluster_changes_block`:

```sql
SELECT citus_cluster_changes_block();
SELECT * FROM citus_cluster_changes_block_status();
-- Snapshot the coordinator and every worker in the same coordinated window.
SELECT citus_cluster_changes_unblock();
```

Always place `unblock` in a `finally` or incident-recovery procedure. Verify the exact semantics for the installed version before production use.

## 15. Failed worker

### The worker can still start

- keep it online;
- repair network, disk, or database problems;
- drain it safely;
- remove it only after placement count reaches zero.

### The worker is lost but a standby or clone exists

- promote according to the HA runbook;
- update node metadata or the provider control plane;
- validate shard consistency;
- rebalance after the cluster is stable.

### The worker is lost and only a backup exists

- restore or perform PITR to the correct point;
- return the node to service or repair it according to the runbook;
- do not remove metadata before understanding which shards had only one surviving copy.

### The worker is lost and unique shards have no backup or replica

Citus cannot recreate missing data merely by calling `citus_remove_node`. State the data-loss risk explicitly.

## 16. Change-ticket checklist

Every change or runbook should record:

- objective and table/node scope;
- version and capability checks;
- baseline measurements;
- preflight commands;
- expected duration and resource headroom;
- checkpointed execution commands;
- stop conditions;
- validation commands;
- rollback procedure;
- monitoring owner;
- backup and restore reference.
