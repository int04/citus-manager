# Citus Observability, Security, HA, Backup, and Upgrades

A distributed PostgreSQL system must be observable and recoverable at three levels:

1. logical Citus objects and routing;
2. PostgreSQL state on every node;
3. infrastructure and network health.

Do not declare the cluster healthy from coordinator CPU or one SQL query alone.

## 1. Observability model

Monitor by workload and by node.

### Workload signals

- throughput by operation or endpoint;
- p50, p95, and p99 latency;
- error and retry rate;
- single-shard versus multi-shard ratio;
- task count and intermediate-result behavior;
- rows/bytes ingested;
- partition creation, retention, and conversion success;
- top queries by total and mean execution time;
- distributed transaction and deadlock frequency.

### Coordinator/receiving-node signals

- CPU, memory, disk, network;
- active/idle/waiting connections;
- planner and coordinator-finalization time;
- intermediate results and temp files;
- metadata synchronization state;
- connection pool pressure to workers;
- long transactions and lock waits;
- background jobs and DDL.

### Worker signals

- CPU saturation and imbalance;
- memory and cache behavior;
- disk latency, IOPS, throughput, and free space;
- WAL generation and retention;
- shard size and placement imbalance;
- autovacuum/analyze health;
- lock waits and long-running tasks;
- replication slots/standbys;
- network latency and errors.

### Cluster-wide signals

- active/inactive nodes;
- metadata sync failures;
- shard and colocation health;
- rebalance/drain progress;
- failed task retries;
- prepared transactions;
- backup age and restore-test status;
- version/configuration drift.

## 2. Core metadata inventory

### Nodes

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

Prefer `citus_nodes` when available for a human-friendly view, but use `pg_dist_node` for low-level diagnosis.

### Citus tables

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

Do not assume all columns exist in every release. Inspect `information_schema.columns` when scripting across versions.

### Shards and placements

```sql
SELECT table_name,
       shardid,
       nodename,
       nodeport,
       shard_size
FROM citus_shards
ORDER BY table_name, shardid, nodename, nodeport;
```

### Node-level balance

```sql
SELECT nodename,
       nodeport,
       count(*) AS placements,
       pg_size_pretty(coalesce(sum(shard_size), 0)::bigint) AS total_size,
       max(shard_size) AS largest_shard_bytes
FROM citus_shards
GROUP BY nodename, nodeport
ORDER BY sum(shard_size) DESC NULLS LAST;
```

Count balance and byte balance are different. Workload/heat balance can differ from both.

## 3. Query statistics

### PostgreSQL statistics

```sql
SELECT queryid,
       calls,
       total_exec_time,
       mean_exec_time,
       rows,
       shared_blks_hit,
       shared_blks_read,
       temp_blks_written,
       left(query, 300) AS query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 50;
```

### Citus statistics

When available:

```sql
SELECT *
FROM citus_stat_statements
ORDER BY calls DESC
LIMIT 50;
```

```sql
SELECT *
FROM citus_stat_counters;
```

Use a controlled measurement window. Record when counters reset and whether statistics are collected on every query-receiving node.

## 4. Current activity, locks, and long transactions

```sql
SELECT pid,
       usename,
       application_name,
       client_addr,
       state,
       wait_event_type,
       wait_event,
       xact_start,
       query_start,
       now() - xact_start AS xact_age,
       now() - query_start AS query_age,
       left(query, 300) AS query
FROM pg_stat_activity
WHERE datname = current_database()
  AND pid <> pg_backend_pid()
ORDER BY xact_start NULLS LAST, query_start NULLS LAST;
```

When available:

```sql
SELECT * FROM citus_lock_waits;
```

Do not terminate a backend until the blocking chain, transaction purpose, and recovery effect are understood.

## 5. Internal connections

Inspect capability-gated Citus connection views/functions and standard activity:

```sql
SELECT * FROM citus_remote_connection_stats();
```

```sql
SELECT application_name,
       client_addr,
       state,
       count(*) AS connections
FROM pg_stat_activity
WHERE datname = current_database()
GROUP BY application_name, client_addr, state
ORDER BY connections DESC;
```

Monitor:

- coordinator-to-worker connection creation/reuse;
- waits for worker connections;
- application pool size per query node;
- session fan-out caused by high shard counts;
- connection storms after deploy/restart/failover;
- idle-in-transaction sessions;
- total headroom against worker `max_connections`.

## 6. Cluster health checks

Capability-gated:

```sql
SELECT *
FROM citus_check_cluster_node_health()
WHERE result = false;
```

This can perform pairwise node checks and create many connections. On large clusters, schedule carefully and do not run it continuously at peak load.

Check extension/config drift on workers:

```sql
SELECT *
FROM run_command_on_workers(
  $$SELECT version() || ' | Citus ' || citus_version()$$
);
```

Manual propagation helpers bypass normal abstraction boundaries. Use them for bounded read-only diagnostics, not casual changes.

## 7. WAL, replication slots, and prepared transactions

```sql
SELECT slot_name,
       slot_type,
       database,
       active,
       restart_lsn,
       confirmed_flush_lsn,
       wal_status,
       safe_wal_size
FROM pg_replication_slots
ORDER BY slot_name;
```

Column availability varies by PostgreSQL release.

```sql
SELECT transaction,
       gid,
       prepared,
       owner,
       database
FROM pg_prepared_xacts
ORDER BY prepared;
```

Monitor during:

- online shard movement;
- logical replication migrations;
- multi-shard 2PC;
- backup windows;
- worker failure/recovery.

Stale slots can retain WAL until disk fills. Prepared transactions can retain locks and block maintenance. Do not delete either without tracing ownership and recovery semantics.

## 8. Rebalance and background jobs

```sql
SELECT * FROM get_rebalance_table_shards_plan();
SELECT * FROM citus_rebalance_status();
```

When background job views exist:

```sql
SELECT * FROM citus_background_jobs ORDER BY job_id DESC;
SELECT * FROM citus_background_task ORDER BY job_id DESC, task_id;
```

Verify actual object names/columns in the installed release.

Observe:

- moved and remaining shards;
- source/target nodes;
- bytes and duration;
- task retries/errors;
- WAL and replication slots;
- disk/network/CPU;
- application latency and connection waits.

## 9. Partition lifecycle observability

Monitor:

- missing future partitions;
- rows in a default partition;
- partition bounds/gaps/overlaps;
- active partition size and ingest rate;
- heap/columnar access method by partition;
- retention/archival job status;
- last analyze/vacuum;
- relation and planning-time growth.

Use `scripts/04-partition-health.sql` and the design in `03-partitioning-and-time-series.md`.

## 10. Security principles

### Network

- keep worker ports on private networks;
- restrict sources with firewall/security groups;
- avoid exposing workers directly to the public Internet;
- use stable internal DNS or addresses;
- monitor unexpected client addresses;
- separate application, administration, backup, and replication paths where practical.

### TLS

Use TLS where required by threat model and policy, including node-to-node connections. Inspect:

```sql
SHOW ssl;
SHOW citus.node_conninfo;
```

`citus.node_conninfo` should contain nonsensitive libpq options such as SSL mode and connection timeouts. Do not put passwords into shared configuration or output.

Validate:

- CA trust and hostname verification;
- certificate renewal;
- worker and coordinator settings;
- managed-service endpoint requirements;
- failover/clone certificates;
- application pool behavior after renewal.

### Authentication metadata

`pg_dist_authinfo.authinfo` can contain clear-text libpq connection parameters. Never print it in logs, tickets, screenshots, or model output.

Safe status query:

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

### Roles and least privilege

Separate:

- cluster/topology administrator;
- schema migration owner;
- application read/write role;
- read-only/analytics role;
- backup/replication role;
- monitoring role.

Ensure application roles exist and are usable wherever distributed queries execute. Review object ownership and `search_path`. Do not let normal application roles modify Citus metadata.

### Secrets

- use a secret manager or protected environment mechanism;
- rotate credentials through a tested process;
- avoid shell history and command-line passwords;
- redact logs and support bundles;
- never commit `.env`, certificates, or connection strings;
- verify rotation across every node and query path.

## 11. High availability model

Citus sharding is not automatically high availability. A unique shard placement on one worker is still a single point of data loss unless protected by physical replication or another tested recovery mechanism.

Design HA per node role:

### Coordinator/metadata node

Protect:

- Citus metadata;
- local and managed-local table data;
- DDL/control endpoint;
- active transactions and application routing.

A query coordinator is not the same as a physical standby. Use PostgreSQL streaming replication or provider HA for actual failover durability.

### Worker

Each worker should have a failure strategy:

- streaming standby and promotion;
- provider-managed HA;
- replacement from a recent backup/PITR;
- clone/snapshot process supported by the installed version;
- accepted data-loss window documented explicitly.

### Load balancing and failover

Separate endpoints when necessary:

- application DML/SELECT endpoint;
- migration/DDL/topology endpoint;
- read-only analytics endpoint;
- monitoring/administration endpoint.

Ensure failover preserves the hostname/port expectations in Citus metadata or uses an approved `citus_update_node()`/provider process.

## 12. Failure scenarios to test

- coordinator process restart;
- coordinator primary failure and standby promotion;
- worker primary failure during a single-shard write;
- worker failure during multi-shard 2PC;
- worker failure during rebalance/drain;
- network partition between coordinator and one worker;
- network partition between worker pairs in query-from-any-node mode;
- replication slot lag and WAL disk pressure;
- DNS or certificate failure;
- backup restore with a different topology;
- application retry after an uncertain commit result.

Record recovery time, data-loss behavior, manual steps, and application symptoms.

## 13. Backup principles

A Citus backup must account for:

- coordinator metadata;
- local/managed-local data;
- every worker holding shard placements;
- distributed transactions in flight;
- extensions, roles, schemas, functions, and configuration;
- tablespaces and columnar relations;
- topology and node identity;
- encryption keys and secret recovery outside the database.

A snapshot of one worker is not a cluster backup. Drain/rebalance is not backup.

## 14. Physical backup and PITR

For physical recovery:

- establish a backup/PITR method for every node;
- coordinate recovery targets across nodes;
- retain WAL adequately;
- document topology reconstruction;
- test consistency after restore;
- test with distributed writes and DDL occurring near the recovery point.

Capability-gated restore point:

```sql
SELECT citus_create_restore_point('<RESTORE_POINT_NAME>');
```

Use only after verifying the installed signature and release semantics.

## 15. Coordinated storage snapshots

Some Citus releases provide cluster-wide change blocking for consistent disk snapshots.

Capability scan:

```sql
SELECT n.nspname,
       p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname IN (
  'citus_cluster_changes_block',
  'citus_cluster_changes_block_status',
  'citus_cluster_changes_unblock'
)
ORDER BY p.proname;
```

Conceptual sequence:

1. confirm no incompatible operation is active;
2. block relevant cluster changes;
3. verify block status;
4. snapshot coordinator and every worker in one bounded window;
5. unblock in a `finally`/failure-safe path;
6. validate snapshots and perform a restore test.

Do not leave a block active after a failed automation step.

## 16. Logical backups

Logical dump/restore can be appropriate for:

- schema export;
- small datasets;
- selective migration;
- version transitions that require logical restore.

But validate:

- restore ordering for extensions and metadata;
- whether to restore logical tables through the coordinator rather than physical shard names;
- table distribution and partition creation order;
- access methods and columnar options;
- roles/ownership/privileges;
- large-data duration and WAL;
- application cutover and consistency.

Do not dump low-level Citus metadata and replay it blindly into a different topology.

## 17. Restore test

A backup is not accepted until restored.

Test:

- node startup and version compatibility;
- extension creation/upgrade;
- Citus metadata and node mapping;
- local/reference/distributed/schema/partitioned/columnar tables;
- row counts and aggregates by key/range;
- routing to expected shards;
- application login and transactions;
- foreign keys and constraints;
- backup age/RPO and recovery duration/RTO;
- PITR around distributed transactions;
- runbook clarity for another operator.

## 18. Upgrade planning

Separate:

- PostgreSQL minor update;
- Citus patch/minor update within a release line;
- Citus major update;
- PostgreSQL major upgrade;
- operating-system/container/provider update;
- extension dependency updates.

### Preflight

- read official release and upgrade notes;
- list versions on every node;
- list extensions and dependencies;
- check breaking changes, removed GUCs/UDFs, and SQL behavior;
- confirm supported PostgreSQL/Citus pairing;
- inspect prepared transactions, slots, rebalance/jobs, and long transactions;
- test backup and restore;
- rehearse with production-like data and application tests.

### Consistency

Do not intentionally mix incompatible PostgreSQL major or Citus extension versions in one cluster. Follow official node/extension upgrade ordering for the target releases.

### Post-upgrade validation

- `version()`, `citus_version()`, and `pg_extension` agree;
- metadata is synchronized;
- nodes are active;
- distributed/reference/local/schema tables are accessible;
- key query plans and latency are within thresholds;
- rebalance/drain capability works in staging;
- backups and replicas are healthy;
- no obsolete GUC/function calls remain in automation;
- application migrations use the correct control endpoint.

## 19. Configuration drift

Collect from all nodes:

- PostgreSQL and Citus versions;
- `shared_preload_libraries`;
- `wal_level`;
- `max_connections`;
- `max_prepared_transactions`;
- replication sender/slot settings;
- Citus GUCs;
- extensions;
- collation/locale where relevant;
- role presence and privileges;
- TLS settings.

Use read-only worker queries carefully. Store sanitized snapshots in configuration management, not secrets.

## 20. Alerting suggestions

Alert on:

- node inactive or metadata not synchronized;
- failed health check;
- disk/WAL free-space thresholds;
- replication lag or inactive slot growth;
- high connection utilization or pool wait;
- long transactions and lock waits;
- prepared transactions older than policy;
- rebalance/drain job failure or stall;
- placement on a node marked not to hold shards;
- shard/node size skew;
- missing future partition or default-partition rows;
- retention/columnar conversion job failure;
- backup age or failed restore test;
- version/config drift;
- coordinator/worker p95/p99 latency regression.

Thresholds must come from workload and recovery objectives, not generic percentages alone.

## 21. Security/HA/backup review checklist

- [ ] Workers are reachable only from approved networks.
- [ ] TLS and certificate verification meet policy.
- [ ] `pg_dist_authinfo` contents are never exposed.
- [ ] Roles follow least privilege and exist on required nodes.
- [ ] Coordinator and every worker have a tested failure strategy.
- [ ] Query coordinator is not mistaken for a standby.
- [ ] Backup covers metadata, local data, and all shard placements.
- [ ] PITR or snapshot coordination is defined and tested.
- [ ] Restore tests validate routing and distributed transactions.
- [ ] RPO/RTO are measured, not assumed.
- [ ] Upgrade ordering is based on official target-version guidance.
- [ ] Version/configuration drift is monitored.
- [ ] Failover, rebalance interruption, and uncertain commit retries were tested.
- [ ] Alerts cover partitions, WAL, slots, prepared transactions, connections, and node health.
