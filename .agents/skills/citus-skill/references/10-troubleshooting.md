# Citus Troubleshooting

Use `scripts/00-capability-scan.sql` and the focused inventory scripts for reproducible read-only evidence collection.

Investigate from evidence: preserve the exact error and SQLSTATE, identify the node that raised it, and collect metadata and resource state before making changes.

## 1. Initial evidence checklist

```sql
SELECT version();
SELECT citus_version();
SELECT current_database(), current_user, inet_server_addr(), inet_server_port();

SELECT * FROM pg_dist_node ORDER BY nodeid;
SELECT * FROM citus_tables ORDER BY table_name;
SELECT * FROM citus_rebalance_status();
SELECT * FROM citus_lock_waits;
```

```sql
SELECT pid,
       usename,
       client_addr,
       state,
       wait_event_type,
       wait_event,
       now() - query_start AS runtime,
       left(query, 300) AS query
FROM pg_stat_activity
WHERE datname = current_database()
ORDER BY query_start NULLS LAST;
```

Also record:

- the exact SQL and parameters;
- the timestamp and time zone;
- whether the relevant log is from the coordinator or a worker;
- disk, CPU, memory, and network state on the affected node;
- recent deployments, migrations, or rebalancing operations;
- whether the query includes the distribution key.

---

## 2. `function ... does not exist`

### Common causes

- the Citus version differs from the documentation;
- the function signature changed;
- the extension was not created in the current database;
- search path or schema mismatch;
- the connection points to the wrong node or database;
- a managed service does not expose the function.

### Checks

```sql
SELECT extname, extversion
FROM pg_extension
WHERE extname = 'citus';
```

```sql
SELECT n.nspname,
       p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname ILIKE '%<FUNCTION_FRAGMENT>%'
ORDER BY p.proname, arguments;
```

### Resolution

- use the signature provided by the installed version;
- create the extension in the correct database when it is missing;
- use the managed-service control plane when node UDFs are intentionally restricted;
- do not copy a command from another version and attempt to force it with arbitrary casts.

---

## 3. `password authentication failed` while adding, activating, or querying a worker

### Checks

- the role exists on the target node;
- the actual PostgreSQL password is correct, not merely the environment variable or secret value;
- `pg_hba.conf`, TLS, and the authentication method are compatible;
- DNS, IP address, and port are correct;
- the same database exists on the target node;
- `pg_dist_authinfo` is configured for the correct node ID and role without exposing the secret;
- a direct `psql` connection succeeds from the source node.

```sql
SELECT rolname, rolcanlogin
FROM pg_roles
WHERE rolname = '<ROLE_NAME>';
```

```sql
SELECT nodeid,
       rolename,
       CASE WHEN authinfo IS NULL OR authinfo = ''
            THEN 'empty' ELSE 'configured' END AS credential_status
FROM pg_dist_authinfo
ORDER BY nodeid, rolename;
```

### Important note

Changing a Docker/Kubernetes secret or `.env` file does not automatically change the role password in an existing database or volume. When required:

```sql
ALTER ROLE <ROLE_NAME>
WITH PASSWORD '<NEW_SECRET>';
```

Never send the secret to chat or logs.

---

## 4. A worker tries to connect to `localhost`

A cluster that started as single-node may still advertise the coordinator as localhost.

```sql
SELECT citus_set_coordinator_host(
  '<REACHABLE_COORDINATOR_HOST>',
  <COORDINATOR_PORT>
);
```

Then inspect `pg_dist_node` and test worker-to-coordinator network connectivity.

---

## 5. A worker was added successfully but remains empty

This is usually expected. Adding a node does not move existing shards automatically.

### Check

```sql
SELECT nodename,
       nodeport,
       count(*) AS placements,
       pg_size_pretty(COALESCE(sum(shard_size), 0)::bigint) AS bytes
FROM citus_shards
GROUP BY nodename, nodeport
ORDER BY nodename, nodeport;
```

### Resolution

```sql
SELECT * FROM get_rebalance_table_shards_plan();
SELECT citus_rebalance_start();
SELECT * FROM citus_rebalance_status();
SELECT citus_rebalance_wait();
```

If the plan is empty, inspect `shouldhaveshards`, the selected strategy, relation filters, node capacity, and cluster state.

---

## 6. Rebalance or drain fails because of replica identity

### Symptoms

- logical replication reports that replica identity is required;
- one table in the colocation group lacks a primary key or suitable unique identity;
- concurrent updates or deletes cannot be replicated safely.

### Find the related tables

```sql
SELECT table_name,
       colocation_id,
       distribution_column
FROM citus_tables
WHERE colocation_id = <COLOCATION_ID>
ORDER BY table_name;
```

Inspect each table's constraints:

```sql
SELECT conname,
       contype,
       pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = '<SCHEMA>.<TABLE>'::regclass
ORDER BY contype, conname;
```

### Preferred resolution order

1. add a suitable primary or unique key that includes the distribution column;
2. configure an appropriate replica identity when the data model permits it;
3. retry the move or rebalance;
4. use `shard_transfer_mode => 'block_writes'` only during a maintenance window when the identity cannot be fixed.

Do not default to `REPLICA IDENTITY FULL` on a large table without benchmarking its write and WAL cost.

---

## 7. Rebalance is stalled or extremely slow

### Inspect jobs and tasks

```sql
SELECT * FROM citus_rebalance_status();
SELECT * FROM pg_dist_background_job ORDER BY job_id DESC;
SELECT * FROM pg_dist_background_task ORDER BY job_id DESC, task_id;
```

### Inspect database resources

```sql
SELECT * FROM pg_replication_slots;
SELECT * FROM pg_stat_activity;
SELECT * FROM citus_lock_waits;
```

Outside SQL, inspect:

- source and target disk latency and free space;
- network throughput and packet loss;
- WAL growth;
- worker processes and background executors;
- long transactions holding `xmin` or locks;
- authentication or DNS timeouts.

### Resolution

- do not kill the source database immediately;
- stop the job in a controlled way with `citus_rebalance_stop()` when necessary;
- resolve resource, lock, authentication, or replica-identity issues;
- reduce transfer parallelism or move the work off-peak;
- regenerate the plan because cluster state may have changed.

---

## 8. `citus_remove_node` reports that the node still has shard placements

This is a correct safety guardrail.

```sql
SELECT table_name,
       shardid,
       shard_size
FROM citus_shards
WHERE nodename = '<WORKER_HOST>'
  AND nodeport = <WORKER_PORT>
ORDER BY shard_size DESC;
```

Drain or move every shard, verify a count of `0`, and only then remove the node. Never edit `pg_dist_*` tables directly to force metadata removal.

---

## 9. Primary key or UNIQUE constraint does not include the distribution column

### Incorrect example

A table is distributed by `tenant_id`, but uses:

```sql
PRIMARY KEY (record_id)
```

### Correct pattern

```sql
PRIMARY KEY (tenant_id, record_id)
```

```sql
UNIQUE (tenant_id, external_code)
```

Before changing constraints, check duplicates under the composite key and review application or ORM mappings.

---

## 10. A foreign key cannot be created

### Possible causes

- the two distributed tables are not colocated;
- distribution keys or data types differ;
- the foreign key does not include the distribution key;
- the local/reference/distributed table combination is unsupported for that layout;
- the foreign key was created before distribution, producing a dependency chain that is difficult to convert.

### Check

```sql
SELECT table_name,
       citus_table_type,
       distribution_column,
       colocation_id,
       shard_count
FROM citus_tables
WHERE table_name IN (
  '<SCHEMA>.<PARENT>'::regclass,
  '<SCHEMA>.<CHILD>'::regclass
);
```

### Resolution

- align distribution keys and data types;
- colocate the tables correctly;
- use composite primary and foreign keys that include the distribution key;
- convert a small shared lookup to a reference table when appropriate;
- recreate the constraint after the table layout is stable.

---

## 11. A query is slow because it lacks the distribution key

### Symptoms

- the task count spans many or all shards;
- many worker connections are opened;
- the coordinator receives many tuples;
- the query filters by `record_id` while the table is distributed by `tenant_id`.

### Check

```sql
EXPLAIN (ANALYZE, VERBOSE, BUFFERS)
<QUERY>;
```

### Resolution order

1. pass the tenant or entity key from the application;
2. add the key to join predicates;
3. add an index that starts with the key where the query requires it;
4. create a mapping from global ID to tenant when the API knows only the global ID;
5. use a denormalized or summary table;
6. revisit the distribution strategy if the real workload usually cannot know the key.

Do not merely increase shard parallelism; that can increase connection use and load.

---

## 12. A cross-shard join is slow or unsupported

### Checks

- do both tables use the same distribution key, type, shard count, and colocation group;
- does the join condition include the distribution key;
- can a small table become a reference table;
- how large is the intermediate result;
- does the plan perform repartitioning.

```sql
SELECT table_name,
       distribution_column,
       colocation_id,
       shard_count
FROM citus_tables
WHERE table_name IN (
  '<SCHEMA>.<TABLE_A>'::regclass,
  '<SCHEMA>.<TABLE_B>'::regclass
);
```

### Resolution

- colocate the tables and join by the distribution key;
- make a small dimension a reference table;
- precompute or materialize the result;
- move the workload to a separate offline analytics path;
- use repartition joins only when intermediate data remains controlled.

---

## 13. Query reaches `max_connections` or times out opening connections

### Checks

```sql
SHOW max_connections;
SELECT * FROM citus_remote_connection_stats();
```

```sql
SELECT client_addr, usename, state, count(*)
FROM pg_stat_activity
GROUP BY client_addr, usename, state
ORDER BY count(*) DESC;
```

```sql
SELECT name, setting
FROM pg_settings
WHERE name IN (
  'citus.max_shared_pool_size',
  'citus.local_shared_pool_size',
  'citus.max_adaptive_executor_pool_size',
  'citus.max_cached_conns_per_worker',
  'citus.executor_slow_start_interval'
);
```

### Possible causes

- too many client connections;
- multi-shard query fan-out;
- shard count is too high for the concurrency level;
- too many cached connections;
- reporting queries monopolize the pool;
- rebalancing or repartitioning needs headroom.

### Resolution order

1. pool application connections;
2. route more queries to a single shard;
3. limit reporting roles with the adaptive executor pool;
4. configure an appropriate shared-pool limit;
5. reduce unnecessary shard/task counts;
6. increase `max_connections` only when memory and reserved headroom permit it.

Increasing `max_connections` does not fix an incorrect fan-out pattern.

---

## 14. Queries become slower after adding a worker

### Possible causes

- the cluster has not been rebalanced;
- the new worker is slower;
- network latency is higher;
- caches are cold;
- shard movement is still running;
- connection count increased;
- node capacities differ;
- multi-shard tail latency is determined by the slowest worker.

### Checks

- compare placements before and after;
- inspect disk, CPU, and network per node;
- use `citus.explain_all_tasks` to find slow tasks;
- inspect rebalance status;
- inspect connection statistics.

### Resolution

- complete or adjust the rebalance;
- repair worker resource or network issues;
- use a capacity-aware strategy where supported;
- do not mix an underpowered worker into a latency-sensitive group without an appropriate strategy.

---

## 15. Shard or node skew

### Inspect shard sizes

```sql
WITH per_shard AS (
  SELECT table_name,
         shardid,
         max(shard_size)::numeric AS bytes
  FROM citus_shards
  GROUP BY table_name, shardid
)
SELECT table_name,
       count(*) AS shards,
       round(max(bytes) / NULLIF(avg(bytes), 0), 2) AS max_to_avg
FROM per_shard
GROUP BY table_name
ORDER BY max_to_avg DESC NULLS LAST;
```

### Possible causes

- uneven distribution key;
- one large tenant;
- too few shards;
- a newly added worker has not received data;
- balancing by shard count despite very different shard sizes.

### Resolution

- rebalance with `by_disk_size`;
- isolate the hot tenant;
- increase shard count through a planned operation;
- change the distribution model only when skew is systemic;
- use a custom capacity strategy for heterogeneous workers.

---

## 16. Coordinator CPU or disk use is high

### Possible causes

- large local tables;
- many cross-shard aggregates or sorts;
- large intermediate results;
- too many client connections;
- planning across too many shards;
- metadata and query-node workload concentrated on one machine.

### Checks

- top queries and plans;
- temporary files and blocks;
- local-table sizes;
- single-shard versus multi-shard counters;
- connection statistics.

### Resolution

- improve routing and pushdown;
- reclassify local and distributed tables;
- pre-aggregate repeated workloads;
- pool connections;
- reduce shard over-fragmentation;
- scale the coordinator only after fixing the data model and queries.

---

## 17. A managed local table cannot be read from a query node

### Checks

- was `citus_add_local_table_to_metadata` called for the table;
- are `hasmetadata`, `metadatasynced`, and `isactive` correct on the query node;
- can the role authenticate from the query node to the coordinator;
- does it have `SELECT` permission;
- is DDL being run only through the appropriate control endpoint.

```sql
SELECT logicalrelid::regclass,
       partmethod,
       colocationid
FROM pg_dist_partition
WHERE logicalrelid = '<SCHEMA>.<TABLE>'::regclass;
```

### Resolution

- add the local table to Citus metadata;
- repair metadata synchronization and authentication;
- grant the required privileges;
- do not create a manual copy on the query node, because it will diverge from the source data.

---

## 18. Residual local data remains after `create_distributed_table`

Newer versions may copy rows into shards while residual local rows remain on the coordinator. Distributed queries do not use those rows, and they may later cause constraint conflicts.

### Before removing them

- confirm the table type and shard layout;
- validate row counts or checksums through the logical table;
- inspect foreign-key cascade scope;
- confirm backup coverage.

### Remove residual local rows

```sql
SELECT truncate_local_data_after_distributing_table(
  '<SCHEMA>.<TABLE>'
);
```

Do not run the function merely because local storage exists; first prove that the distribution migration succeeded.

---

## 19. Metadata is not synchronized

### Symptoms

- a query node is missing a table, function, or type;
- `metadatasynced=false`;
- DDL ran on the wrong node;
- node addition failed partway through.

### Checks

```sql
SELECT nodeid,
       nodename,
       nodeport,
       hasmetadata,
       metadatasynced,
       isactive
FROM pg_dist_node
ORDER BY nodeid;
```

Find synchronization functions available in the installed version:

```sql
SELECT p.proname,
       pg_get_function_identity_arguments(p.oid)
FROM pg_proc AS p
WHERE p.proname ILIKE '%metadata%sync%'
ORDER BY p.proname;
```

### Resolution

- use the synchronization function supported by the installed version;
- fix authentication, network, or object dependencies first;
- run DDL through the control endpoint;
- never edit metadata tables directly.

---

## 20. Disk fills or WAL grows rapidly during shard movement

### Possible causes

- insufficient target headroom;
- logical-replication WAL retained for too long;
- a replication slot is not advancing;
- transfer parallelism is too high;
- a long transaction retains WAL;
- source and target shard copies temporarily coexist.

### Checks

```sql
SELECT * FROM pg_replication_slots;
SELECT * FROM citus_rebalance_status();
```

```sql
SELECT pid,
       usename,
       xact_start,
       now() - xact_start AS xact_age,
       state,
       left(query, 200) AS query
FROM pg_stat_activity
WHERE xact_start IS NOT NULL
ORDER BY xact_start;
```

### Resolution

- stop or reduce transfers in a controlled way;
- resolve long-running transactions;
- add disk or WAL capacity;
- repair broken replication;
- do not drop an active replication slot before understanding its owner and purpose.

---

## 21. Stale prepared transactions

```sql
SELECT *
FROM pg_prepared_xacts
ORDER BY prepared;
```

Do not issue `COMMIT PREPARED` or `ROLLBACK PREPARED` arbitrarily. Determine the distributed transaction decision from the coordinator, metadata, logs, and the recovery runbook.

---

## 22. Block-read, relation-file, or corruption errors on a worker

Errors such as `could not read blocks`, short reads, or damaged relation files are not ordinary rebalancer logic failures.

### Actions

1. stop the move or drain operation that is adding load;
2. preserve the node and volume for investigation;
3. inspect disk, filesystem, kernel, and container-storage logs;
4. run appropriate PostgreSQL integrity and relation checks;
5. identify the affected physical shard and logical table;
6. restore or replace it from a replica or backup when available;
7. resume draining only after the source data is readable or has been recovered.

Do not delete the container or volume to “reinstall” it when it may hold the only copy of a shard.

---

## 23. Incident-response conclusion template

Codex should summarize an incident with:

- **Symptom:** exact error and operation.
- **Evidence-backed cause:** relevant metadata, log, and resource findings.
- **Remaining hypotheses:** ordered by likelihood.
- **Next checks:** read-only commands with the target node stated explicitly.
- **Safe remediation:** checkpointed steps.
- **Do not do:** actions that could cause data loss.
- **Recovery criteria:** query, placement, health, and resource state return to acceptable values.
