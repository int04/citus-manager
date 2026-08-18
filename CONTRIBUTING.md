# Contributing to Citus Manager

Thank you for helping improve Citus Manager. Contributions may include bug
reports, documentation, tests, translations, and code.

## Before You Start

- Search existing [issues](https://github.com/int04/citus-manager/issues) and
  pull requests before opening a duplicate.
- Use an issue to discuss substantial features, behavioral changes, database
  migrations, or changes to operation safety guarantees before implementation.
- Report security vulnerabilities privately as described in
  [SECURITY.md](SECURITY.md), not in a public issue.

## Development Setup

Prerequisites:

- .NET SDK 10
- A dedicated PostgreSQL database for Citus Manager's control data
- Node.js and npm only when rebuilding the SQL console editor bundle

Configure secrets through environment variables or a local secret manager.
Never commit credentials, tokens, production connection strings, database
dumps, or Data Protection keys.

```powershell
$env:ConnectionStrings__ControlDatabase='Host=localhost;Port=5432;Database=citus_manager;Username=citus_manager;Password=<SECRET>'
$env:Security__DataProtectionKeyPath='D:\protected\citus-manager-keys'
dotnet restore
dotnet run --launch-profile http
```

The development configuration can create or migrate the control schema. Use a
disposable control database and test cluster; do not point a development build
at production infrastructure.

To rebuild the checked-in SQL editor bundle:

```powershell
npm ci
npm run build:query-console
```

## Making a Change

1. Fork the repository and create a focused branch from the default branch.
2. Keep the change small and avoid unrelated formatting or generated-file
   churn.
3. Add or update tests and documentation for changed behavior.
4. Preserve the operation engine's safety properties: capability checks,
   immutable plans, live preflight, cluster locking, checkpoints, and explicit
   recovery states.
5. Run the relevant checks locally.

Recommended checks:

```powershell
dotnet build CitusManager.sln --configuration Release
dotnet test CitusManager.sln --configuration Release --no-restore
dotnet list CitusManager.sln package --vulnerable --include-transitive
docker compose config --quiet
```

Run `docker compose config --quiet` when changing Compose configuration. When a
change requires a live Citus cluster, describe the PostgreSQL and Citus
versions, topology, test data, and observed result in the pull request.

## Pull Requests

A pull request should include:

- A concise problem statement and explanation of the solution.
- Links to relevant issues.
- User-visible, API, schema, compatibility, security, and operational impact.
- Tests run and their results. Distinguish unit tests from live-cluster or
  end-to-end validation.
- Screenshots for user-interface changes.
- Rollback or recovery notes for changes that affect persisted data or cluster
  operations.

Maintainers may request that broad changes be split into smaller pull requests.
All submissions are reviewed for correctness, safety, maintainability, and
license compatibility. By submitting a contribution, you agree that it may be
distributed under the repository's [0BSD license](LICENSE).

## Style and Documentation

- Follow existing C# and ASP.NET Core conventions in the repository.
- Keep public behavior and API documentation synchronized.
- Keep the English README canonical and update `README_VI.md` when shared
  behavior, setup steps, or safety guidance changes.
- Use demo data in screenshots and redact hosts, usernames, tokens, cluster
  identifiers, and other sensitive values.

## Conduct

Participation in this project is governed by the
[Code of Conduct](CODE_OF_CONDUCT.md).
