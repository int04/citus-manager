# Citus Architecture and Capability Model

Use this reference before making design or operational assumptions. Citus extends PostgreSQL rather than replacing it, so every design has two layers:

1. PostgreSQL behavior inside each node and shard;
2. Citus behavior for routing, parallel execution, metadata, data movement, and distributed transactions.

## 1. First decision: do you need a distributed database?

Citus is justified when one or more measured constraints cannot be solved economically on one PostgreSQL server:

- the working set, indexes, or retained data exceed one node's practical storage or memory envelope;
- CPU-bound queries need parallel execution across independent workers;
- write throughput is limited by one node and writes can be partitioned by a stable key;
- tenant/entity isolation and scale-out are central requirements;
- operational scale requires moving logical units such as tenants or schemas between nodes;
- a future growth plan needs horizontal capacity with bounded application changes.

Do not choose multi-node Citus solely because:

- a table is large but cold;
- one query is missing an index;
- autovacuum is misconfigured;
- a time-series table needs retention management;
- connection pooling is absent;
- vertical scaling remains materially cheaper and simpler;
- the workload has no useful locality key and every transaction touches arbitrary rows.

Compare these alternatives first:

| Option | Best fit | Main limitation |
|---|---|---|
| Plain PostgreSQL | Moderate scale, broad SQL flexibility, simplest operations | One machine's capacity |
| PostgreSQL partitioning | Pruning, retention, maintenance isolation within one server | Does not add worker CPU/storage |
| Single-node Citus | Citus compatibility, local experimentation, columnar use | No horizontal worker capacity |
| Row-based multi-node Citus | Dense multitenancy, entity-centric OLTP, time series, parallel analytics | Requires a distribution key and query discipline |
| Schema-based multi-node Citus | Tenant/service per schema, heterogeneous schemas, minimal key changes | Lower tenant density and weaker cross-schema flexibility |
| Separate services/databases | Strong domain isolation and independent scaling | Cross-domain consistency and query complexity |

## 2. Logical architecture

### Coordinator or receiving node

The receiving node accepts SQL, reads Citus metadata, and decides whether to:

- route a query to one worker/shard;
- decompose it into multiple tasks;
- execute worker fragments in parallel;
- materialize or pull intermediate results;
- finalize aggregation, sorting, or joins locally.

In traditional deployments, applications connect to a coordinator. In query-from-any-node/MX-style deployments, metadata-capable nodes can receive supported queries. Do not infer that every node is allowed to execute DDL or topology changes; verify the installed version and deployment model.

### Workers

Workers store shard relations and execute PostgreSQL plans for task fragments. Each worker is a complete PostgreSQL server. Index design, statistics, vacuum, memory, WAL, locks, extensions, and storage performance still matter on every worker.

### Metadata

Citus metadata maps:

- logical tables to distribution methods and colocation groups;
- logical shards to hash ranges or shard identities;
- shard placements to worker groups;
- nodes to roles, addresses, active state, metadata state, and placement eligibility.

High-level views such as `citus_tables`, `citus_shards`, and `citus_nodes` are usually easier to consume. Low-level catalogs such as `pg_dist_partition`, `pg_dist_shard`, `pg_dist_placement`, `pg_dist_node`, and `pg_dist_colocation` are essential for deeper diagnosis.

### Shards and placements

A shard is a logical partition of a distributed table. A placement is a physical instance of that shard on a node. Keep these terms separate:

- increasing `shard_count` changes logical data subdivision;
- moving a placement changes where a shard lives;
- adding a worker changes available capacity but does not necessarily move existing placements;
- rebalancing changes placement distribution and can move colocated shards together.

### Colocation groups

Colocated tables share compatible shard boundaries and placement. Equal distribution values land on the same node across the group. This enables local joins and transaction locality, but also couples data movement and shard-count changes.

## 3. Query execution paths

### 3.1. Single-shard/router

A predicate provides a concrete distribution-key value and all relevant distributed tables are compatible. Citus can target one shard placement.

Typical strengths:

- lowest network and task overhead;
- full PostgreSQL SQL surface on the target worker in many cases;
- simple transaction locality;
- predictable connection use.

### 3.2. Colocated distributed

Multiple shards participate, but joins and partial aggregation can execute on matching colocated placements. This is the preferred path for large distributed joins.

### 3.3. Reference-table join

A reference table is present on workers, allowing local joins with distributed shards. Reference tables are appropriate only when their size and write rate remain suitable for full replication.

### 3.4. Multi-shard parallel

Tasks run across many shards, and the receiving node combines results. This can be effective for analytics when worker computation dominates task and connection overhead.

### 3.5. Repartition or cross-shard

Rows must move between workers or be repartitioned because join/group keys do not align with data placement. Treat this as a design warning for hot paths. It can still be valid for bounded analytical work.

### 3.6. Coordinator-heavy or push-pull

Large intermediate results, non-pushdown expressions, recurring relations, CTEs/subqueries, global order, or unsupported forms can cause work to concentrate on the receiving node. Measure intermediate-result size and coordinator CPU/memory/network.

## 4. Sharding models

### Row-based sharding

A distribution column hashes rows into shards. Use it when many tenants/entities share a schema and most hot operations can carry the same key.

Advantages:

- high tenant/entity density;
- strong hardware efficiency;
- explicit single-shard routing;
- colocated joins and transactions;
- parallel scans across workers.

Costs:

- distribution key must propagate through the schema and queries;
- global uniqueness and cross-key foreign keys are constrained;
- cross-tenant/entity transactions can become distributed;
- a poor key creates skew or fan-out.

### Schema-based sharding

A distributed schema is assigned to a colocation group/node, and tables inside the schema become colocated without a row-level shard key.

Advantages:

- tenant/service schemas can remain structurally independent;
- fewer application query changes;
- single-column keys and intra-schema relationships can remain natural;
- heterogeneous schemas are possible.

Costs:

- lower tenant density;
- cross-schema joins and foreign keys require careful compatibility checks;
- large schemas can become indivisible hot units;
- schema count and DDL propagation become operational concerns.

### Hybrid use

A database can contain local, managed-local, reference, row-distributed, schema-distributed, partitioned, and columnar relations. Do not assume every combination is supported for every constraint or query. Verify each relationship.

## 5. Per-database extension model

Citus is enabled per PostgreSQL database. A node can host multiple databases, but:

- the database must exist where required;
- the Citus extension version must be compatible;
- node registration and metadata are database-specific;
- roles, extensions, types, functions, and schemas used by distributed objects must exist or propagate correctly;
- creating a new database does not automatically copy the Citus cluster definition from another database.

Always confirm `current_database()`, server address, and extension state before diagnosing a missing function or node.

## 6. Capability detection

Treat documentation as a candidate behavior and the connected database as the source of truth.

### Version and extension inventory

```sql
SELECT version();
SELECT citus_version();
SELECT current_database(), current_user,
       inet_server_addr(), inet_server_port();

SELECT extname, extversion
FROM pg_extension
ORDER BY extname;
```

### Function discovery

```sql
SELECT n.nspname AS schema_name,
       p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments,
       pg_get_function_result(p.oid) AS result_type,
       p.prokind
FROM pg_proc AS p
JOIN pg_namespace AS n ON n.oid = p.pronamespace
WHERE p.proname ILIKE '%<FUNCTION_FRAGMENT>%'
ORDER BY p.proname, arguments;
```

### View/catalog discovery

```sql
SELECT to_regclass('pg_catalog.pg_dist_node') AS pg_dist_node,
       to_regclass('public.citus_tables') AS citus_tables,
       to_regclass('public.citus_shards') AS citus_shards,
       to_regclass('public.citus_nodes') AS citus_nodes;
```

Schema placement can vary. Use `pg_class` plus `pg_namespace` when `to_regclass` under the current `search_path` returns `NULL`.

### GUC discovery

```sql
SELECT name, setting, unit, context, source, pending_restart
FROM pg_settings
WHERE name LIKE 'citus.%'
ORDER BY name;
```

### Table capability inventory

```sql
SELECT table_name,
       citus_table_type,
       distribution_column,
       colocation_id,
       shard_count,
       table_size,
       access_method
FROM citus_tables
ORDER BY table_name;
```

Do not use a column from a compatibility view without checking that it exists in the installed version. For reusable scripts, query `information_schema.columns` first or keep separate version branches.

## 7. Managed service boundary

Managed services may:

- hide superuser privileges;
- replace node-management SQL with a control plane;
- restrict direct worker access;
- expose a different endpoint model;
- automate HA, backups, upgrades, TLS, or rebalancing;
- omit community functions or add provider-specific functions.

For managed environments:

1. detect what SQL is exposed;
2. identify provider-owned operations;
3. use provider documentation for topology and recovery;
4. still apply the same data-model, query-locality, shard, partition, and validation reasoning.

## 8. Version-sensitive feature classes

Always verify capabilities for:

- schema-based sharding and schema DDL propagation;
- query-from-any-node behavior;
- online shard transfer modes;
- snapshot/clone-based worker addition;
- background rebalance APIs and arguments;
- cluster-wide change blocking and restore points;
- outer-join pushdown and recursive planning behavior;
- time-partition helper signatures;
- columnar update/delete/index support;
- metadata view columns;
- distributed function behavior;
- managed local table relationships.

## 9. Architecture review questions

Before approving a design, answer:

1. What measured limit requires Citus?
2. What key defines the smallest useful unit of data and transaction locality?
3. Which queries become single-shard?
4. Which queries remain multi-shard or cross-shard, and how often do they run?
5. Which tables must be colocated, referenced, local, or independent?
6. How is uniqueness enforced across shards and partitions?
7. How many logical relations will exist after shards, partitions, and indexes multiply?
8. What is the connection fan-out at peak concurrency?
9. What is the largest tenant/entity/schema, and can it be moved or isolated?
10. How are retention, backup, restore, worker failure, and upgrades tested?
11. What is the migration and rollback boundary?
12. Which assumptions depend on a specific Citus release or provider?

A design that cannot answer these questions is not ready for production execution.
