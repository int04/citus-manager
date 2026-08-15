# Citus Data Modeling

This guide helps choose a reusable distribution model for any project, independent of table names or application frameworks. Read `01-architecture-and-capability-model.md` first when the deployment or supported feature set is unclear. Use `03-partitioning-and-time-series.md` for the complete partition design and lifecycle method.

## 1. Choose the sharding model before choosing commands

### Row-based sharding

Use one distribution column to hash rows into shards.

Use it when:

- many tenants, customers, or entities share one schema;
- most queries and transactions can be associated with one key;
- a large table must scale across multiple workers;
- related tables need colocated joins.

Common distribution-key candidates include:

- `tenant_id`;
- `customer_id`;
- `account_id`;
- `organization_id`;
- `conversation_id`;
- `device_id` or another stable entity key.

### Schema-based sharding

Each distributed schema is assigned to a colocation group or node without requiring a distribution column on every table.

Use it when:

- each tenant has its own schema;
- a microservice or database module is isolated by schema;
- schemas are largely independent;
- single-column primary keys and intra-schema relationships should remain unchanged.

Do not choose schema-based sharding merely to avoid adding `tenant_id`. A very large schema count, cross-schema queries, or oversized tenants require separate evaluation.

### No sharding

Do not distribute every table simply because the Citus extension is installed. Some tables should remain local or reference tables, and some small databases may not need distribution yet.

---

## 2. Table-type selection matrix

| Type | Data location | Use when | Risks or contraindications |
|---|---|---|---|
| Local | Coordinator/control node | Small administrative tables, configuration, internal queues, and tables that do not participate in hot joins with distributed tables | The coordinator becomes a bottleneck when the table is large or heavily read/written |
| Managed local | Data remains on the coordinator while Citus manages metadata | Other query nodes must access the local table, or it needs supported foreign-key relationships with managed local/reference tables | Adds metadata dependency; data still does not scale horizontally |
| Reference | One complete shard is replicated to every worker | Small shared lookup or dimension tables frequently joined with distributed tables | Writes must be coordinated across nodes; unsuitable for large or write-heavy tables |
| Distributed | Multiple shards on workers | Large tables that scale by tenant/entity and queries that usually include a routing key | A poor key causes fan-out, skew, and constraint limitations |
| Schema table | Table inside a distributed schema | Tenant-per-schema or service-per-schema designs | Cross-schema workloads and schema counts must be controlled |
| Columnar distributed/local | Columnar storage in the chosen layout | Append-heavy analytics and large scans/aggregations | Index, update, and delete limitations; not a default OLTP choice |

### Quick rules

- Large table with a strong routing key → distributed.
- Small shared lookup needed on every worker → reference.
- Small control or administration table → local.
- Local table that must be read from another query node → managed local.
- Clearly isolated tenant schemas → consider schema-based sharding.

---

## 3. Choose the distribution column

The distribution column is the most important design decision. Score each candidate against the following criteria.

### 3.1. Routing frequency

Does the column appear in most hot queries?

Good:

```sql
WHERE tenant_id = $1
```

Poor:

```sql
WHERE status = $1
```

when the table is distributed by `tenant_id` and the query does not know the tenant.

### 3.2. Join and transaction locality

Can the same key appear on every table that must be joined or updated in one transaction?

Example transaction scoped to `tenant_id`:

```sql
BEGIN;
UPDATE app.accounts
SET balance = balance - $2
WHERE tenant_id = $1
  AND account_id = $3;

INSERT INTO app.ledger(tenant_id, entry_id, account_id, amount)
VALUES ($1, $4, $3, -$2);
COMMIT;
```

When both tables are colocated by `tenant_id`, the transaction can often remain single-shard and incur less distributed overhead.

### 3.3. Cardinality and distribution

The key should have many distinct values and distribute data reasonably evenly.

Avoid:

- booleans;
- statuses;
- country or region when only a few values dominate;
- day or month values;
- nullable keys;
- a default value that owns most rows.

High cardinality does not guarantee balance. Measure rows, bytes, and traffic per key.

### 3.4. Immutability

The distribution value should not change frequently. Moving a row to another tenant or entity may require a delete-and-insert workflow or a dedicated migration.

### 3.5. Ability to propagate the key through the schema

When a child table lacks the key, consider denormalizing it into that table to enable:

- colocated joins;
- composite foreign keys;
- router queries;
- explicit tenant isolation.

### 3.6. Hot-tenant risk

One exceptionally large tenant can still make a shard hot. Possible responses include:

- isolate the tenant into its own shard;
- move that shard to a stronger worker;
- separate its workload or adjust the key model;
- for one extremely large tenant, consider a finer-grained key when transaction locality permits it.

Do not redesign the entire cluster around one outlier before testing tenant isolation.

---

## 4. Common anti-patterns

### Using a timestamp as the distribution key

Hashing or ranging by time usually fails to keep one tenant/entity together and makes colocation difficult.

A common better design is:

- Citus hash distribution by tenant/entity;
- PostgreSQL range partitioning by `created_at` or `event_time`.

### Distributing by a global sequence while querying by tenant

If the table is distributed by `id` while nearly every query uses `tenant_id`, one tenant can span many shards and every request may fan out.

### Using a low-cardinality key

For example, `status` with five values provides too few distribution units and is likely to create skew.

### Using a mutable key

For example, `region_id` is a poor choice when entities frequently move between regions. Updating a distribution column is not equivalent to updating an ordinary column.

### Giving every table a different key

This breaks colocation, distributed foreign keys, and transaction locality. Use different keys only when the tables serve genuinely independent workloads.

---

## 5. Primary keys, UNIQUE constraints, and foreign keys

### 5.1. Primary and UNIQUE constraints must include the distribution column

For a table distributed by `tenant_id`:

```sql
PRIMARY KEY (tenant_id, record_id)
```

Do not expect this to be globally unique across the cluster:

```sql
PRIMARY KEY (record_id)
```

Likewise:

```sql
UNIQUE (tenant_id, external_code)
```

### 5.2. Foreign keys between distributed tables

The two tables should:

- use the same distribution column;
- use the same data type;
- belong to the same colocation group;
- include the distribution column in the foreign key.

Example:

```sql
CREATE TABLE app.parent (
  tenant_id bigint NOT NULL,
  parent_id bigint NOT NULL,
  PRIMARY KEY (tenant_id, parent_id)
);

CREATE TABLE app.child (
  tenant_id bigint NOT NULL,
  child_id bigint NOT NULL,
  parent_id bigint NOT NULL,
  PRIMARY KEY (tenant_id, child_id),
  FOREIGN KEY (tenant_id, parent_id)
    REFERENCES app.parent(tenant_id, parent_id)
);
```

### 5.3. Foreign keys to reference tables

A distributed table can reference lookup/reference tables in layouts supported by Citus. Converting the lookup table to a reference table before adding the foreign key is usually the clearest sequence.

### 5.4. Replica identity

Online shard movement and rebalancing through logical replication require a suitable primary key or replica identity. Designing the key correctly from the start simplifies operations later.

---

## 6. Colocation

Colocation keeps corresponding shards of related tables on the same workers so joins and transactions can execute locally.

### 6.1. Create a deliberate colocation group

Root table:

```sql
SELECT create_distributed_table(
  'app.root_entity',
  'tenant_id',
  colocate_with => 'none',
  shard_count => 64
);
```

Related table:

```sql
SELECT create_distributed_table(
  'app.related_entity',
  'tenant_id',
  colocate_with => 'app.root_entity'
);
```

### 6.2. When to colocate

Colocate tables when they:

- join frequently by the distribution key;
- participate in the same transactions;
- need distributed-to-distributed foreign keys;
- should scale and rebalance together.

### 6.3. When not to colocate

Separate tables with `colocate_with => 'none'` when:

- they do not join each other;
- their workloads and growth rates differ;
- they should be rebalanced independently;
- their keys share a type only by coincidence.

Implicit colocation can cause unrelated tables to move together.

### 6.4. Audit colocation

```sql
SELECT table_name,
       distribution_column,
       colocation_id,
       shard_count
FROM citus_tables
ORDER BY colocation_id, table_name;
```

Inspect whether any colocation group contains tables with no real business relationship.

---

## 7. Choose the shard count

The shard count is the number of logical units Citus can distribute and parallelize within a colocation group.

### 7.1. Required inputs

Collect:

- current worker count and the 12–24 month target;
- total worker CPU cores;
- current data size and growth rate;
- concurrent query count;
- single-shard versus multi-shard ratio;
- current shard sizes;
- acceptable movement and recovery time;
- `max_connections` and internal pool limits.

### 7.2. Starting point for multi-tenant workloads

A common test range is 32–128 shards per colocation group:

- workloads below roughly 100 GB may begin testing at 32;
- larger workloads or plans to use more workers may test 64 or 128;
- benchmark with real queries and the expected growth plan.

Do not assume 32 shards are always sufficient. A table with 32 primary shards cannot place primary shards across all 100 workers at the same time.

### 7.3. Starting point for analytics

A common experiment is a shard count around 2–4 times the total worker CPU cores, followed by measurement of:

- CPU utilization;
- task latency;
- connection fan-out;
- coordinator planning time;
- network and intermediate-result volume;
- skew.

### 7.4. Connection budget

A conservative estimate is:

```text
peak_internal_connections
≈ concurrent_multi_shard_queries
  × effective_connections_per_query_per_worker
```

A simple upper-bound check that can expose an oversized design is:

```text
concurrent_multi_shard_queries × shards_touched_per_query
< worker_count × usable_connections_per_worker
```

`usable_connections_per_worker` must reserve headroom for:

- clients and administrators;
- autovacuum;
- replication and WAL senders;
- background jobs and rebalancing;
- monitoring;
- repartition queries.

### 7.5. Too few shards

Symptoms include:

- workers or CPU cores remain underused;
- individual shards are very large and slow to move or recover;
- newly added workers cannot receive an even share of a table;
- a hot shard cannot be divided easily.

### 7.6. Too many shards

Symptoms include:

- high planning time;
- thousands of tasks for queries without the distribution key;
- connection exhaustion;
- high metadata and rebalancing overhead;
- many tiny shards;
- fragmented autovacuum and maintenance work.

### 7.7. Change the shard count after creation

Newer Citus versions support:

```sql
SELECT alter_distributed_table(
  'app.root_entity',
  shard_count => 128,
  cascade_to_colocated => true
);
```

This moves data. Before running it, verify:

- the exact version documentation and function signature;
- disk headroom;
- replica identity;
- WAL and network capacity;
- the maintenance window;
- every table in the colocation group.

---

## 8. Combine PostgreSQL partitioning with Citus

Citus sharding and PostgreSQL partitioning divide data along different dimensions:

- Citus distributes tenants or entities across workers;
- PostgreSQL partitioning divides data inside each shard by time or another domain.

Common design:

```sql
CREATE TABLE app.events (
  tenant_id bigint NOT NULL,
  event_id bigint NOT NULL,
  event_time timestamptz NOT NULL,
  payload jsonb,
  PRIMARY KEY (tenant_id, event_id, event_time)
) PARTITION BY RANGE (event_time);
```

Create the partitioned parent, distribute the parent by `tenant_id`, and then create or attach the leaf partitions when the installed version supports that layout. Verify the exact sequence on staging because conversion of an existing populated partition tree is materially different from greenfield creation.

Benefits include:

- fast retention through partition drops;
- time-based partition pruning;
- tenant-based shard pruning;
- indexes and maintenance on smaller partitions.

Do not create an excessive `partition count × shard count` combination without evaluating metadata and planning overhead.

---

## 9. Neutral row-based template

```sql
CREATE SCHEMA IF NOT EXISTS app;

CREATE TABLE app.tenants (
  tenant_id bigint NOT NULL,
  name text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id)
);

CREATE TABLE app.records (
  tenant_id bigint NOT NULL,
  record_id bigint NOT NULL,
  category_code text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  payload jsonb,
  PRIMARY KEY (tenant_id, record_id)
);

CREATE TABLE app.record_items (
  tenant_id bigint NOT NULL,
  record_id bigint NOT NULL,
  item_no integer NOT NULL,
  value numeric(18,2) NOT NULL,
  PRIMARY KEY (tenant_id, record_id, item_no)
);

CREATE TABLE app.categories (
  category_code text PRIMARY KEY,
  display_name text NOT NULL
);
```

Distribute the tables:

```sql
SELECT create_reference_table('app.categories');

SELECT create_distributed_table(
  'app.tenants',
  'tenant_id',
  colocate_with => 'none',
  shard_count => 64
);

SELECT create_distributed_table(
  'app.records',
  'tenant_id',
  colocate_with => 'app.tenants'
);

SELECT create_distributed_table(
  'app.record_items',
  'tenant_id',
  colocate_with => 'app.records'
);
```

Add foreign keys after the layout is correct:

```sql
ALTER TABLE app.records
ADD CONSTRAINT fk_records_tenant
FOREIGN KEY (tenant_id)
REFERENCES app.tenants(tenant_id);

ALTER TABLE app.records
ADD CONSTRAINT fk_records_category
FOREIGN KEY (category_code)
REFERENCES app.categories(category_code);

ALTER TABLE app.record_items
ADD CONSTRAINT fk_record_items_record
FOREIGN KEY (tenant_id, record_id)
REFERENCES app.records(tenant_id, record_id);
```

Add indexes for hot queries:

```sql
CREATE INDEX ix_records_tenant_created
ON app.records(tenant_id, created_at DESC);
```

---

## 10. Migrate an existing schema

1. Inventory tables, sizes, primary keys, foreign keys, unique constraints, and indexes.
2. Collect top queries from `pg_stat_statements` and application code.
3. Choose the distribution strategy before calling `create_distributed_table`.
4. Backfill the distribution key into tables that do not have it.
5. Update primary keys, foreign keys, and unique constraints for the new key.
6. Classify tables as reference, local, or distributed.
7. Create colocation groups in dependency order.
8. Distribute the schema in staging with a representative copy of production data.
9. Validate row counts, constraints, and query plans.
10. Only then design the production migration, cutover, and rollback.

### Useful audit queries

List constraints for one table:

```sql
SELECT conname,
       contype,
       pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE conrelid = '<SCHEMA>.<TABLE>'::regclass
ORDER BY contype, conname;
```

List indexes:

```sql
SELECT indexname, indexdef
FROM pg_indexes
WHERE schemaname = '<SCHEMA>'
  AND tablename = '<TABLE>'
ORDER BY indexname;
```

Do not convert every table automatically in one huge transaction without testing lock duration, runtime, and data movement.
