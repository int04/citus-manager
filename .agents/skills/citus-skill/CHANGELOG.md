# Changelog

All notable changes to this project are documented here.

The project follows semantic versioning for the skill package itself. Citus and PostgreSQL compatibility is detected at runtime and is not implied by the package version.

## [1.0.0] - 2026-08-15

### Added

- Version-aware `SKILL.md` for reusable Citus engineering across projects.
- Data modeling for row-based and schema-based sharding.
- Local, managed-local, reference, distributed, partitioned, heap, and columnar table guidance.
- Distribution-key, colocation, integrity, shard-count, and capacity methodology.
- Full PostgreSQL partitioning and time-series design guide for Citus.
- Query routing, indexing, statistics, vacuum, connection, skew, and performance guidance.
- DML, ingestion, distributed transaction, rollup, and distributed-function guidance.
- Columnar and hybrid hot/cold storage guidance.
- Worker, shard, schema, and topology operation runbooks.
- Observability, security, HA, backup, restore, and upgrade guidance.
- Migration and architecture patterns.
- Troubleshooting and command references.
- Advanced SQL, analytics, extensions, RLS, triggers, sequences, and compatibility guidance.
- Read-only diagnostic SQL scripts.
- Deterministic shard, partition, relation, and connection capacity calculator with unit tests.
- Reusable architecture, ADR, migration, experiment, and incident templates.
- Public GitHub documentation, contribution guide, code of conduct, security policy, issue forms, pull request template, CI validation, and MIT license.
- GitHub Actions workflow for package validation on pushes and pull requests.
