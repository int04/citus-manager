# Citus demo dataset

Synthetic dataset for Citus Manager UI/explorer testing. Target database must be a
Citus coordinator with at least one active worker.

## Contents

| Table | Model | Rows | Main coverage |
|---|---:|---:|---|
| `citus_demo.admin_jobs` | local | 5,000 | control-plane jobs, enum, interval, arrays, JSONB, inet |
| `citus_demo.products` | reference | 5,000 | replicated dimension, range, tsvector |
| `citus_demo.tenants` | distributed | 5,000 | root distribution/colocation table |
| `citus_demo.customers` | distributed + colocated | 8,000 | composite FK/unique keys, cidr |
| `citus_demo.orders` | distributed + colocated | 9,000 | enum, numeric, tstzrange, partial index |
| `citus_demo.order_items` | distributed + colocated | 10,000 | distributed/reference foreign keys |
| `citus_demo.sensor_events` | distributed + range-partitioned | 10,000 | shard + partition pruning, BRIN, bytea, point, bit |
| `citus_demo.audit_logs` | independent distributed | 8,000 | broad PostgreSQL scalar/range/network/search types |
| `citus_demo_tenant_blue.invoices` | schema-sharded | 6,000 | schema-based Citus sharding |

Columnar is intentionally absent: seed preflight found no `columnar` access method
in this cluster. All generated values are synthetic.

## Run

Set the password only in the current shell; do not commit it:

```powershell
$env:PGPASSWORD='<cluster-password>'
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_demo_seed.sql
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_demo_validate.sql
```

Seed refuses to overwrite either demo schema. To rebuild, explicitly remove only
the synthetic schemas, then seed again:

```powershell
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_demo_cleanup.sql
```

`citus_demo_cleanup.sql` is destructive for the two demo schemas only.

## Large expansion pack

`citus_scale` adds 21 logical tables and 24,751,000 rows across SaaS, billing,
usage analytics, banking, payments, social/chat, logistics, healthcare, and
customer-support workloads. Table sizes range from 500 rows to 3,000,000 rows.

Highlights:

- five independent/intentional colocation groups with 16 shards each;
- local and reference tables, including a 500,000-row reference dimension;
- 12 monthly usage partitions and eight monthly logistics partitions;
- composite distributed foreign keys and distributed-to-reference foreign keys;
- B-tree, partial, GIN, and BRIN indexes;
- resumable 100,000-row seed batches using deterministic keys and `ON CONFLICT`;
- router, colocated join, shard-pruning, and partition-pruning validation plans.

Run in order:

```powershell
$env:PGPASSWORD='<cluster-password>'
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_scale_schema.sql
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_scale_seed.sql
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_scale_finalize.sql
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_scale_validate.sql
```

The seed phase is restartable. Schema creation intentionally refuses to overwrite
an existing `citus_scale` schema.

Cleanup is destructive for `citus_scale` only:

```powershell
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_scale_cleanup.sql
```

### 44-million-row extension

After the large expansion pack is complete, append another 19,000,000 rows to
the eight hottest fact/event tables:

```powershell
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_scale_extend_44m.sql
psql -X -h localhost -p 5533 -U postgres -d citusdb -v ON_ERROR_STOP=1 -f database/demo/citus_scale_validate_44m.sql
```

Final totals:

- `citus_scale`: 43,751,000 rows;
- all synthetic datasets: 43,817,000 rows;
- largest tables: `usage_events`, `ledger_entries`, and `messages`, 6,000,000 rows each;
- extension remains restartable through deterministic key ranges and
  `ON CONFLICT DO NOTHING`.
