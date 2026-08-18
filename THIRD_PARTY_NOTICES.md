# Third-Party Notices

Citus Manager's original source code is licensed under the
[Zero-Clause BSD license](LICENSE). That license does not replace or override
the licenses of third-party software used by, built into, or distributed with
the project. Each third-party component remains subject to its own license and
copyright notices.

This document records direct dependencies and browser assets identified in the
repository manifests at the time of publication. Transitive dependencies may
also be included in restored, built, or container artifacts. Their authoritative
license metadata and notices are supplied by their packages and upstream
projects. `package-lock.json` is the authoritative npm version lock for a given
checkout.

## Runtime NuGet Dependencies

| Component | Version | License | Upstream |
| --- | ---: | --- | --- |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.10 | MIT | <https://github.com/dotnet/aspnetcore> |
| Microsoft.AspNetCore.OpenApi | 10.0.10 | MIT | <https://github.com/dotnet/aspnetcore> |
| Microsoft.OpenApi | 2.11.0 | MIT | <https://github.com/microsoft/OpenAPI.NET> |
| Microsoft.EntityFrameworkCore.Design | 10.0.10 | MIT | <https://github.com/dotnet/efcore> |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL License | <https://github.com/npgsql/efcore.pg> |
| pgsqlparser | 1.0.0 | MIT | <https://github.com/mysticmind/pgsqlparser-dotnet> |
| CsvHelper | 33.1.0 | MS-PL OR Apache-2.0 | <https://github.com/JoshClose/CsvHelper> |
| AWSSDK.S3 | 4.0.102.1 | Apache-2.0 | <https://github.com/aws/aws-sdk-net> |

`Microsoft.EntityFrameworkCore.Design` is marked as a private asset in the
project manifest but is listed because it participates in development and build
tooling.

## Test NuGet Dependencies

These packages are used by the test project and are not intended as application
runtime dependencies.

| Component | Version | License | Upstream |
| --- | ---: | --- | --- |
| Microsoft.NET.Test.Sdk | 18.8.1 | MIT | <https://github.com/microsoft/vstest> |
| xunit | 2.9.3 | Apache-2.0 | <https://github.com/xunit/xunit> |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | <https://github.com/xunit/visualstudio.xunit> |

## npm Dependencies and Generated SQL Editor Bundle

The checked-in SQL editor bundle under `wwwroot/js/vendor/` is generated from
the following direct dependencies. Versions below are the resolved versions in
`package-lock.json`, not merely the compatible ranges in `package.json`.

| Component | Resolved version | License | Upstream |
| --- | ---: | --- | --- |
| @codemirror/autocomplete | 6.20.3 | MIT | <https://github.com/codemirror/autocomplete> |
| @codemirror/commands | 6.10.4 | MIT | <https://github.com/codemirror/commands> |
| @codemirror/lang-sql | 6.10.0 | MIT | <https://github.com/codemirror/lang-sql> |
| @codemirror/language | 6.12.4 | MIT | <https://github.com/codemirror/language> |
| @codemirror/search | 6.7.1 | MIT | <https://github.com/codemirror/search> |
| @codemirror/state | 6.7.1 | MIT | <https://github.com/codemirror/state> |
| @codemirror/view | 6.43.8 | MIT | <https://github.com/codemirror/view> |
| codemirror | 6.0.2 | MIT | <https://github.com/codemirror/basic-setup> |
| sql-formatter | 15.8.2 | MIT | <https://github.com/sql-formatter-org/sql-formatter> |
| esbuild (development/build tool) | 0.25.12 | MIT | <https://github.com/evanw/esbuild> |

The generated bundle can include transitive npm components. Consult
`package-lock.json` and the corresponding package distributions for their full
license texts and notices.

## Vendored Browser Assets

| Component | Version | License | Local notice/source |
| --- | ---: | --- | --- |
| Bootstrap | 5.3.3 | MIT | `wwwroot/lib/bootstrap/LICENSE`; <https://github.com/twbs/bootstrap> |
| jQuery | 3.7.1 | MIT | `wwwroot/lib/jquery/LICENSE.txt`; <https://github.com/jquery/jquery> |
| jQuery Validation | 1.21.0 | MIT | `wwwroot/lib/jquery-validation/LICENSE.md`; <https://github.com/jquery-validation/jquery-validation> |
| jQuery Validation Unobtrusive | 4.0.0 | See supplied notices | `wwwroot/lib/jquery-validation-unobtrusive/LICENSE.txt`; <https://github.com/aspnet/jquery-validation-unobtrusive> |
| Font Awesome | 4.7.0 | Font: SIL OFL 1.1; CSS: MIT | `wwwroot/lib/font-awesome/README.md`; <https://github.com/FortAwesome/Font-Awesome> |

The vendored jQuery Validation Unobtrusive distribution contains a bundled MIT
license file and an Apache-2.0 license reference in its JavaScript header. Both
notices should be preserved when redistributing that asset.

## Maintaining This File

When adding, removing, or upgrading a dependency:

1. Update the applicable manifest and lock file.
2. Verify license metadata against the distributed package, not an assumption
   based on the publisher.
3. Preserve required copyright, attribution, and license files in distributed
   artifacts.
4. Update this notice when a direct dependency, resolved browser-bundle version,
   vendored asset, or license changes.

This notice is provided for transparency and is not legal advice.
