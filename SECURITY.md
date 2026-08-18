# Security Policy

## Supported Versions

Security fixes are applied to the default branch and, when a container release
is available, to the latest published container image. Older source revisions,
commit-pinned images, and historical image tags may not receive fixes.

Users should track the latest release, keep PostgreSQL, Citus, container hosts,
and reverse proxies patched, and review release notes before upgrading.

## Reporting a Vulnerability

Do not disclose a suspected vulnerability in a public issue, discussion, pull
request, log excerpt, or screenshot.

Use GitHub's private vulnerability reporting form:

<https://github.com/int04/citus-manager/security/advisories/new>

If private vulnerability reporting is unavailable, contact the maintainer
privately through the contact method published on the
[int04 GitHub profile](https://github.com/int04). Do not send sensitive details
through a public GitHub issue.

Include, when possible:

- The affected commit, release, or container tag.
- A description of the impact and required attacker access.
- Reproduction steps or a minimal proof of concept.
- Relevant configuration with all credentials and personal data removed.
- Any known mitigations or workarounds.

The maintainer will assess the report, coordinate validation and remediation,
and discuss disclosure timing with the reporter. Please allow time for a fix to
be prepared and distributed before public disclosure.

## Security Scope

Reports about authentication or authorization bypass, credential disclosure,
unsafe SQL execution outside documented permissions, secret leakage, backup
confidentiality or integrity, operation-engine safety bypass, and dependency
vulnerabilities with a demonstrated impact on Citus Manager are in scope.

Citus Manager administers infrastructure supplied by its operator. Issues in
PostgreSQL, Citus, Docker, cloud storage, SMTP, Telegram, Prometheus, or another
upstream service should normally be reported to that project unless Citus
Manager introduces or materially amplifies the vulnerability.

Operational hardening—including TLS termination, network isolation, database
role privileges, secret management, and backups of the control database and
Data Protection keyring—remains the deployer's responsibility.
