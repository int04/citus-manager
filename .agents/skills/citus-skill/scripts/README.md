# Diagnostics and Deterministic Planning Tools

The SQL scripts collect evidence without intentionally changing database state. Run them in the database where the Citus extension is installed.

```bash
psql "$DATABASE_URL" -X -v ON_ERROR_STOP=1 \
  -f scripts/00-capability-scan.sql
```

Recommended order:

1. `00-capability-scan.sql`
2. `01-cluster-inventory.sql`
3. `02-table-and-colocation-inventory.sql`
4. `03-shard-skew-and-placement.sql`
5. `04-partition-health.sql`
6. `05-query-and-connection-diagnostics.sql`
7. `06-topology-change-preflight.sql`
8. `07-safe-auth-audit.sql`

## Capacity model

`capacity_model.py` performs deterministic arithmetic only. It does not connect to PostgreSQL, inspect a cluster, or recommend a production configuration.

```bash
python3 scripts/capacity_model.py \
  --workers 4 \
  --cores-per-worker 8 \
  --max-connections-per-worker 200 \
  --reserved-connections-per-worker 40 \
  --shard-count 64 \
  --active-leaf-partitions 12 \
  --indexes-per-leaf-partition 3 \
  --concurrent-multi-shard-queries 5 \
  --shards-touched-per-query 16 \
  --total-logical-data-gb 1024
```

The model reports shard density, a conservative internal connection upper bound, relation fan-out caused by shards and leaf partitions, and average shard sizes. Use `--json` for structured output. Validate all inputs against the installed Citus version, live limits, query plans, and representative concurrency tests.

## Safety notes

- Review every script before running it in production.
- Some catalog/health queries can be expensive on clusters with many nodes, shards, partitions, or active sessions.
- Pairwise cluster health checks are intentionally omitted from the default scripts because they can open many connections.
- The scripts never print `pg_dist_authinfo.authinfo`.
- Optional views are queried only when they exist; output varies by installed version.
- These scripts do not replace OS, storage, network, provider, PostgreSQL log, or application telemetry.
- The capacity model is arithmetic, not a measurement, benchmark, or deployment recommendation.
