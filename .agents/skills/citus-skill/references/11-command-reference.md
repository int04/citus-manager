# Citus Command Reference

Every command is a template. Verify the installed function signature, privileges, node/database target, and managed-service restrictions before execution.

A quick-reference guide for Codex. Every example uses placeholders; replace them with verified values before execution.

## Risk-level conventions

- **READ**: reads metadata or status only.
- **WRITE**: changes metadata, configuration, or data.
- **IMPACT**: may move or delete data, generate significant load, block writes, or alter topology.

Run topology and table-management commands on the system's designated control endpoint or coordinator unless the platform documentation explicitly says otherwise.

---

## 1. Version, extensions, and capabilities

### READ — basic versions

```sql
SELECT version();
SELECT citus_version();

SELECT extname, extversion
FROM pg_extension
WHERE extname IN ('citus', 'pg_stat_statements');
```

### READ — required and commonly inspected settings

```sql
SHOW shared_preload_libraries;
SHOW wal_level;
SHOW max_connections;
SHOW max_prepared_transactions;
SHOW max_worker_processes;
```

### READ — list every Citus GUC available in the installed version

```sql
SELECT name, setting, unit, context, short_desc
FROM pg_settings
WHERE name LIKE 'citus.%'
ORDER BY name;
```

### READ — verify that a function exists and inspect its actual signature

```sql
SELECT n.nspname AS schema_name,
       p.proname AS function_name,
       pg_get_function_identity_arguments(p.oid) AS arguments,
       pg_get_function_result(p.oid) AS result_type
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname = '<FUNCTION_NAME>'
ORDER BY arguments;
```

Use this check before calling newer functions or functions whose parameters may have changed between versions.

---

## 2. Nodes and topology

### READ — complete node list

```sql
SELECT nodeid,
       groupid,
       nodename,
       nodeport,
       noderole,
       nodecluster,
       isactive,
       hasmetadata,
       metadatasynced,
       shouldhaveshards
FROM pg_dist_node
ORDER BY groupid, nodeid;
```

Some columns vary by version. If a column does not exist, fall back to:

```sql
SELECT *
FROM pg_dist_node
ORDER BY nodeid;
```

### READ — human-friendly node view

```sql
SELECT *
FROM citus_nodes
ORDER BY nodename, nodeport;
```

### READ — active workers

```sql
SELECT *
FROM citus_get_active_worker_nodes();
```

### READ — test connectivity between every pair of nodes

```sql
SELECT *
FROM citus_check_cluster_node_health()
WHERE result = false;
```

This function performs approximately `N²` checks. Do not run it continuously on a large cluster.

### READ — run a read-only command on workers

```sql
SELECT *
FROM run_command_on_workers($cmd$
  SELECT version() || ' | Citus ' || citus_version()
$cmd$);
```

```sql
SELECT *
FROM run_command_on_workers($cmd$
  SELECT current_setting('max_connections')
$cmd$);
```

Do not use `run_command_on_workers` for bulk DDL or DML without a reviewed runbook and rollback plan.

### READ — internal coordinator-to-worker connections

```sql
SELECT *
FROM citus_remote_connection_stats();
```

### WRITE — declare the coordinator host for a cluster that started as single-node

```sql
SELECT citus_set_coordinator_host(
  '<COORDINATOR_HOST>',
  <COORDINATOR_PORT>
);
```

### WRITE — add a worker directly

```sql
SELECT citus_add_node(
  '<WORKER_HOST>',
  <WORKER_PORT>
);
```

Adding a node does not automatically move existing shards to it.

### WRITE — add a node as inactive, then activate it

```sql
SELECT citus_add_inactive_node(
  '<WORKER_HOST>',
  <WORKER_PORT>
);
```

```sql
SELECT citus_activate_node(
  '<WORKER_HOST>',
  <WORKER_PORT>
);
```

Use the inactive flow when authentication or metadata must be completed before the node receives traffic or reference tables.

### WRITE — change the hostname or port of a registered node

```sql
SELECT citus_update_node(
  <NODE_ID>,
  '<NEW_HOST>',
  <NEW_PORT>
);
```

### WRITE — temporarily disable or reactivate a node

```sql
SELECT citus_disable_node('<WORKER_HOST>', <WORKER_PORT>);
SELECT citus_activate_node('<WORKER_HOST>', <WORKER_PORT>);
```

Disabling a node is not a replacement for drain/remove when retiring it permanently.

### WRITE — allow or prevent a node from storing shards

```sql
SELECT citus_set_node_property(
  '<WORKER_HOST>',
  <WORKER_PORT>,
  'shouldhaveshards',
  false
);
```

Re-enable it with:

```sql
SELECT citus_set_node_property(
  '<WORKER_HOST>',
  <WORKER_PORT>,
  'shouldhaveshards',
  true
);
```

### READ — confirm that credentials are configured without exposing secrets

```sql
SELECT nodeid,
       rolename,
       CASE
         WHEN authinfo IS NULL OR authinfo = '' THEN 'empty'
         ELSE 'configured'
       END AS credential_status
FROM pg_dist_authinfo
ORDER BY nodeid, rolename;
```

Never select `authinfo` into logs, tickets, or chat messages.

---

## 3. Table types and metadata

### READ — list Citus tables

```sql
SELECT table_name,
       citus_table_type,
       distribution_column,
       colocation_id,
       shard_count,
       table_size,
       table_owner,
       access_method
FROM citus_tables
ORDER BY table_name;
```

If a column is unavailable in the installed version, inspect the available columns with `SELECT * FROM citus_tables LIMIT 1`.

### READ — low-level table metadata

```sql
SELECT logicalrelid::regclass AS table_name,
       partmethod,
       column_to_column_name(logicalrelid, partkey) AS distribution_column,
       colocationid,
       repmodel
FROM pg_dist_partition
ORDER BY logicalrelid::regclass::text;
```

### READ — colocation groups

```sql
SELECT *
FROM pg_dist_colocation
ORDER BY colocationid;
```

### READ — shards and placements

```sql
SELECT table_name,
       shardid,
       nodename,
       nodeport,
       shard_size
FROM citus_shards
ORDER BY table_name, shardid, nodename;
```

### READ — total size and placement count by node

```sql
SELECT nodename,
       nodeport,
       COUNT(*) AS placements,
       pg_size_pretty(COALESCE(SUM(shard_size), 0)::bigint) AS total_size
FROM citus_shards
GROUP BY nodename, nodeport
ORDER BY SUM(shard_size) DESC NULLS LAST;
```

### READ — size by table and node

```sql
SELECT table_name,
       nodename,
       nodeport,
       COUNT(*) AS placements,
       pg_size_pretty(COALESCE(SUM(shard_size), 0)::bigint) AS size
FROM citus_shards
GROUP BY table_name, nodename, nodeport
ORDER BY table_name, nodename, nodeport;
```

### READ — size of one distributed table

```sql
SELECT pg_size_pretty(citus_relation_size('<SCHEMA>.<TABLE>')) AS relation_size,
       pg_size_pretty(citus_table_size('<SCHEMA>.<TABLE>')) AS table_size,
       pg_size_pretty(citus_total_relation_size('<SCHEMA>.<TABLE>')) AS total_with_indexes;
```

### READ — shard that owns a distribution value

```sql
SELECT get_shard_id_for_distribution_column(
  '<SCHEMA>.<TABLE>',
  <TENANT_VALUE>
) AS shard_id;
```

If the function does not exist, inspect version capabilities or use the routing diagnostics supported by that version.

---

## 4. Create and convert table types

### WRITE — create an independent distributed table

```sql
SELECT create_distributed_table(
  '<SCHEMA>.<TABLE>',
  '<DIST_COLUMN>',
  colocate_with => 'none',
  shard_count => <SHARD_COUNT>
);
```

Use `colocate_with => 'none'` when the table is unrelated to any existing colocation group.

### WRITE — create a distributed table colocated with a root table

```sql
SELECT create_distributed_table(
  '<SCHEMA>.<TABLE>',
  '<DIST_COLUMN>',
  colocate_with => '<SCHEMA>.<COLOCATED_TABLE>'
);
```

Do not specify a different `shard_count` when colocating with an existing table.

### WRITE — create a reference table

```sql
SELECT create_reference_table('<SCHEMA>.<TABLE>');
```

Use reference tables only for small, shared tables that are not written to heavily.

### WRITE — add a managed local table

```sql
SELECT citus_add_local_table_to_metadata(
  '<SCHEMA>.<TABLE>'
);
```

Foreign-key cascade variant:

```sql
SELECT citus_add_local_table_to_metadata(
  '<SCHEMA>.<TABLE>',
  cascade_via_foreign_keys => true
);
```

Inspect the cascade scope before using it.

### IMPACT — remove residual local rows after distributing a populated table

```sql
SELECT truncate_local_data_after_distributing_table(
  '<SCHEMA>.<TABLE>'
);
```

Run this only after validating worker data, related constraints and foreign keys, and backup coverage. The function may cascade.

### IMPACT — convert a distributed or reference table back to local

```sql
SELECT undistribute_table('<SCHEMA>.<TABLE>');
```

Foreign-key cascade variant:

```sql
SELECT undistribute_table(
  '<SCHEMA>.<TABLE>',
  cascade_via_foreign_keys => true
);
```

The coordinator must have enough capacity for the data. This is a data-movement operation.

### IMPACT — change the shard count of a table or colocation group

```sql
SELECT alter_distributed_table(
  '<SCHEMA>.<TABLE>',
  shard_count => <NEW_SHARD_COUNT>,
  cascade_to_colocated => true
);
```

`cascade_to_colocated => true` preserves colocation and updates the whole group. Use `false` only when intentionally separating the table from the group.

### IMPACT — change the distribution column

```sql
SELECT alter_distributed_table(
  '<SCHEMA>.<TABLE>',
  distribution_column => '<NEW_DIST_COLUMN>'
);
```

This is a major design change. Review primary keys, unique constraints, foreign keys, routing behavior, data movement, and version-specific lock or downtime behavior.

### WRITE/IMPACT — change colocation

A version-dependent approach that may involve data movement:

```sql
SELECT alter_distributed_table(
  '<SCHEMA>.<TABLE>',
  colocate_with => '<SCHEMA>.<TARGET_TABLE>',
  cascade_to_colocated => false
);
```

Update colocation metadata only when shard layouts are already compatible:

```sql
SELECT update_distributed_table_colocation(
  '<SCHEMA>.<TABLE>',
  colocate_with => '<SCHEMA>.<TARGET_TABLE>'
);
```

Separate a table from its colocation group:

```sql
SELECT update_distributed_table_colocation(
  '<SCHEMA>.<TABLE>',
  colocate_with => 'none'
);
```

### WRITE — set the default shard count for tables created later

For the current session:

```sql
SET citus.shard_count = <SHARD_COUNT>;
```

At the database or role level, only after benchmarking:

```sql
ALTER DATABASE <DB_NAME>
SET citus.shard_count = <SHARD_COUNT>;
```

This setting does not change the shard count of existing tables.

---

## 5. Schema-based sharding

Use only when the installed version supports it and the model fits tenant-per-schema or service-per-schema designs.

### IMPACT — distribute a schema

```sql
SELECT citus_schema_distribute('<SCHEMA_NAME>');
```

### IMPACT — return a distributed schema to the coordinator/local node

```sql
SELECT citus_schema_undistribute('<SCHEMA_NAME>');
```

### IMPACT — move a distributed schema

```sql
SELECT citus_schema_move(
  '<SCHEMA_NAME>',
  '<TARGET_WORKER_HOST>',
  <TARGET_WORKER_PORT>
);
```

If a `shard_transfer_mode` argument is needed, inspect the installed function signature first.

---

## 6. Columnar storage

Use primarily for append-heavy analytical tables. Review index, update/delete, and workload limitations before adopting it.

### IMPACT — switch between heap and columnar access methods

```sql
SELECT alter_table_set_access_method(
  '<SCHEMA>.<TABLE>',
  'columnar'
);
```

Switch back with:

```sql
SELECT alter_table_set_access_method(
  '<SCHEMA>.<TABLE>',
  'heap'
);
```

### READ — columnar options

```sql
SELECT *
FROM columnar.options;
```

Run this only when the installed extension/version provides the `columnar` schema.

---

## 7. Rebalance, move, and drain shards

### READ — inspect available strategies

```sql
SELECT *
FROM pg_dist_rebalance_strategy
ORDER BY name;
```

### READ — preview a rebalance plan

```sql
SELECT *
FROM get_rebalance_table_shards_plan();
```

Limit it to one table when the installed signature supports the parameter:

```sql
SELECT *
FROM get_rebalance_table_shards_plan(
  relation => '<SCHEMA>.<TABLE>'::regclass
);
```

### IMPACT — start a background rebalance

```sql
SELECT citus_rebalance_start();
```

Specify a strategy only when measurements justify it:

```sql
SELECT citus_rebalance_start(
  rebalance_strategy => 'by_disk_size'
);
```

Depending on the version, these options may be available:

```sql
SELECT citus_rebalance_start(
  parallel_transfer_colocated_shards => true,
  parallel_transfer_reference_tables => true
);
```

Do not enable transfer parallelism merely because the function exposes parameters. First inspect CPU, disk, network, WAL, worker-process, and background-executor headroom.

### READ — monitor, wait for, or stop a rebalance

```sql
SELECT *
FROM citus_rebalance_status();
```

```sql
SELECT citus_rebalance_wait();
```

```sql
SELECT citus_rebalance_stop();
```

### IMPACT — drain one node

```sql
SELECT *
FROM citus_drain_node(
  '<WORKER_HOST>',
  <WORKER_PORT>
);
```

When a table lacks replica identity and a maintenance window has been approved:

```sql
SELECT *
FROM citus_drain_node(
  '<WORKER_HOST>',
  <WORKER_PORT>,
  shard_transfer_mode => 'block_writes'
);
```

Do not use `block_writes` as the default transfer mode.

### IMPACT — drain multiple nodes with a background rebalance

Set `shouldhaveshards=false` on every target node, then preview:

```sql
SELECT *
FROM get_rebalance_table_shards_plan(
  drain_only => true
);
```

Start the drain:

```sql
SELECT citus_rebalance_start(
  drain_only => true
);
```

`drain_only` affects every node whose `shouldhaveshards` value is currently `false`.

### READ — verify that a node has no placements left

```sql
SELECT COUNT(*) AS placements_left
FROM citus_shards
WHERE nodename = '<WORKER_HOST>'
  AND nodeport = <WORKER_PORT>;
```

### IMPACT — remove a node after placement count reaches zero

```sql
SELECT citus_remove_node(
  '<WORKER_HOST>',
  <WORKER_PORT>
);
```

### IMPACT — manually move one shard placement

```sql
SELECT citus_move_shard_placement(
  <SHARD_ID>,
  '<SOURCE_HOST>',
  <SOURCE_PORT>,
  '<TARGET_HOST>',
  <TARGET_PORT>
);
```

Use manual moves only for deliberate placement work. Prefer the rebalancer for normal cluster balancing.

### IMPACT — isolate a hot tenant into a dedicated shard

```sql
SELECT isolate_tenant_to_new_shard(
  '<SCHEMA>.<TABLE>',
  <TENANT_VALUE>,
  'CASCADE'
) AS isolated_shard_id;
```

The new shard can then be moved to an appropriate worker. Verify the function signature and the effect on the full colocation group.

---

## 8. Query routing, EXPLAIN, and statistics

### READ — compare queries with and without the distribution key

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
SELECT *
FROM <SCHEMA>.<TABLE>
WHERE <DIST_COLUMN> = <TENANT_VALUE>;
```

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
SELECT *
FROM <SCHEMA>.<TABLE>
WHERE <NON_DIST_COLUMN> = <VALUE>;
```

### READ/SESSION — show the plan for every task

```sql
SET LOCAL citus.explain_all_tasks = on;

EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
<QUERY>;
```

Use only when necessary because the output can be large and the analysis can be expensive.

### READ — Citus query statistics

```sql
SELECT *
FROM citus_stat_statements
ORDER BY calls DESC
LIMIT 50;
```

Column layout may differ by version. Combine it with `pg_stat_statements` when useful:

```sql
SELECT queryid,
       calls,
       total_exec_time,
       mean_exec_time,
       rows,
       left(query, 300) AS query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 50;
```

### WRITE — reset statistics for a new baseline

```sql
SELECT citus_stat_statements_reset();
SELECT pg_stat_statements_reset();
```

### WRITE — enable Citus counters

```sql
ALTER DATABASE <DB_NAME>
SET citus.enable_stat_counters = on;
```

Reconnect, then read the counters:

```sql
SELECT *
FROM citus_stat_counters;
```

Or, when the installed version exposes a function form:

```sql
SELECT *
FROM citus_stat_counters(
  (SELECT oid FROM pg_database WHERE datname = current_database())
);
```

### READ — cluster-wide lock waits

```sql
SELECT *
FROM citus_lock_waits;
```

### READ — current activity

```sql
SELECT pid,
       usename,
       datname,
       client_addr,
       state,
       wait_event_type,
       wait_event,
       query_start,
       now() - query_start AS runtime,
       left(query, 300) AS query
FROM pg_stat_activity
WHERE datname = current_database()
  AND pid <> pg_backend_pid()
ORDER BY query_start NULLS LAST;
```

### READ — background jobs and tasks

```sql
SELECT *
FROM pg_dist_background_job
ORDER BY job_id DESC;
```

```sql
SELECT *
FROM pg_dist_background_task
ORDER BY job_id DESC, task_id;
```

Table or column names may differ in older versions; perform a capability check.

---

## 9. Connection and executor GUCs

### READ — GUCs related to connection fan-out

```sql
SELECT name, setting, unit, short_desc
FROM pg_settings
WHERE name IN (
  'citus.max_shared_pool_size',
  'citus.local_shared_pool_size',
  'citus.max_adaptive_executor_pool_size',
  'citus.max_cached_conns_per_worker',
  'citus.executor_slow_start_interval',
  'citus.force_max_query_parallelization'
)
ORDER BY name;
```

### WRITE/SESSION — constrain a low-priority workload

```sql
SET LOCAL citus.max_adaptive_executor_pool_size = <SMALL_LIMIT>;
```

At the role level, after benchmarking:

```sql
ALTER ROLE <ROLE_NAME>
SET citus.max_adaptive_executor_pool_size = <LIMIT>;
```

### WRITE/SESSION — force maximum parallelism for one measured transaction

```sql
BEGIN;
SET LOCAL citus.force_max_query_parallelization = on;
<MEASURED_ANALYTIC_QUERY>;
COMMIT;
```

Do not enable this globally before measuring total cluster throughput.

### WRITE — throttle coordinator-to-worker connections

```sql
ALTER SYSTEM
SET citus.max_shared_pool_size = <PER_REMOTE_NODE_LIMIT>;

SELECT pg_reload_conf();
```

Use only after reserving headroom for clients, autovacuum, replication, administration, and repartition queries.

---

## 10. Maintenance, statistics, and storage

### WRITE — refresh statistics

```sql
ANALYZE <SCHEMA>.<TABLE>;
```

After a bulk load or a major distribution change:

```sql
VACUUM (ANALYZE) <SCHEMA>.<TABLE>;
```

Assess I/O impact before running this on large tables.

### READ — Citus column statistics

```sql
SELECT *
FROM citus_stats
WHERE tablename = '<TABLE_NAME>'
ORDER BY attname;
```

### READ — replication slots and WAL-related state

```sql
SELECT slot_name,
       plugin,
       slot_type,
       active,
       restart_lsn,
       confirmed_flush_lsn
FROM pg_replication_slots
ORDER BY slot_name;
```

### READ — prepared transactions and two-phase commit

```sql
SELECT *
FROM pg_prepared_xacts
ORDER BY prepared;
```

### READ — index usage on workers

```sql
SELECT *
FROM run_command_on_workers($cmd$
  SELECT schemaname,
         relname,
         indexrelname,
         idx_scan,
         pg_size_pretty(pg_relation_size(indexrelid)) AS index_size
  FROM pg_stat_user_indexes
  ORDER BY pg_relation_size(indexrelid) DESC
  LIMIT 50
$cmd$);
```

Results are returned per worker. Do not accidentally aggregate physical shard indexes as though they were one logical table without accounting for the shard layout.

---

## 11. Distributed functions

Use a distributed function to route execution by a distribution argument and reduce round trips for single-tenant logic.

```sql
SELECT create_distributed_function(
  '<FUNCTION_SIGNATURE>',
  '<DISTRIBUTION_ARGUMENT_NAME>',
  colocate_with => '<SCHEMA>.<TABLE>'
);
```

Mark a function as distributed only after confirming that it does not access data outside the declared routing key.

---

## 12. Advanced snapshot and restore coordination

Some newer Citus versions expose functions that block cluster changes while coordinated snapshots are taken. Always inspect their signatures first:

```sql
SELECT n.nspname,
       p.proname,
       pg_get_function_identity_arguments(p.oid)
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname LIKE 'citus_cluster_changes_%';
```

When available and required by the backup runbook:

```sql
SELECT citus_cluster_changes_block();
SELECT * FROM citus_cluster_changes_block_status();
-- Take coordinated snapshots of the coordinator and every worker.
SELECT citus_cluster_changes_unblock();
```

This does not replace tested restores, PITR, or standby coverage.

---

## 13. PostgreSQL partitioning and Citus time helpers

### READ — inspect a partition tree

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

### WRITE — create future time partitions

Verify the installed signature:

```sql
SELECT n.nspname,
       p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname = 'create_time_partitions';
```

Template:

```sql
SELECT create_time_partitions(
  table_name         := '<SCHEMA>.<PARENT_TABLE>',
  partition_interval := '<PARTITION_INTERVAL>',
  start_from         := <START_BOUND>,
  end_at             := <END_BOUND>
);
```

### READ — list Citus-managed time partitions

```sql
SELECT *
FROM time_partitions
WHERE parent_table = '<SCHEMA>.<PARENT_TABLE>'::regclass
ORDER BY partition;
```

Inspect the view's actual columns first when scripting across versions.

### DESTRUCTIVE — drop expired time partitions

```sql
CALL drop_old_time_partitions(
  '<SCHEMA>.<PARENT_TABLE>',
  now() - <RETENTION_INTERVAL>
);
```

Preview partition bounds and sizes before calling the procedure. Confirm retention, backup/archive, dependency, lock, and restore requirements.

### IMPACT — convert old partitions to another access method

```sql
CALL alter_old_partitions_set_access_method(
  '<SCHEMA>.<PARENT_TABLE>',
  now() - <HOT_WINDOW>,
  'columnar'
);
```

Use only after proving the selected partitions are immutable and columnar-compatible.

### WRITE/IMPACT — attach a partition

```sql
ALTER TABLE <SCHEMA>.<PARENT_TABLE>
ATTACH PARTITION <SCHEMA>.<PARTITION_TABLE>
FOR VALUES FROM (<LOWER_BOUND>) TO (<UPPER_BOUND>);
```

PostgreSQL can scan the table to validate bounds and acquires locks. A validated matching `CHECK` constraint can sometimes avoid the scan. Test the exact distributed hierarchy.

### IMPACT — detach a partition

```sql
ALTER TABLE <SCHEMA>.<PARENT_TABLE>
DETACH PARTITION <SCHEMA>.<PARTITION_TABLE>;
```

`CONCURRENTLY` is PostgreSQL-version and layout sensitive. Detaching changes query visibility and is not equivalent to a backup.

---

## 14. Snapshot/clone-based node addition

Newer Citus releases may expose clone/snapshot helpers that reduce full data copy when adding capacity. Discover them:

```sql
SELECT n.nspname,
       p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname IN (
  'citus_add_clone_node',
  'citus_add_clone_node_with_nodeid',
  'get_snapshot_based_node_split_plan',
  'citus_promote_clone_and_rebalance'
)
ORDER BY p.proname, arguments;
```

Do not synthesize commands from function names. Follow the exact target-version cluster-management guide and rehearse physical replication, registration, promotion, failure, and cleanup.

---

## 15. Manual query propagation

### READ — run a bounded diagnostic on workers

```sql
SELECT *
FROM run_command_on_workers($cmd$
  SHOW work_mem;
$cmd$);
```

### High-risk propagation functions

Citus exposes helpers that can execute SQL on workers, shards, placements, or all nodes. These can bypass coordinator planning, locking, dependency propagation, and consistency checks.

Before any non-read-only use:

- inspect the function signature and official target-version documentation;
- prove the exact target set;
- prove idempotency;
- account for partial failure;
- use fully qualified object names;
- record worker-level validation;
- define repair for inconsistent completion.

Do not use manual propagation to “fix” missing DDL until the propagation failure and metadata/object registration are understood.
