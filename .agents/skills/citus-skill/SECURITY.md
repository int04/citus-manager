# Security Policy

## Reporting a security issue

Do not open a public issue containing credentials, private topology, production SQL logs, customer data, or exploit details. Use a private GitHub Security Advisory for the repository that publishes this skill.

## Scope

This repository contains instructions, reference material, templates, and read-only diagnostic scripts. Security issues can still arise from unsafe operational guidance, secret disclosure, destructive defaults, or commands that misrepresent their risk.

A useful report includes:

- the affected file and section;
- the unsafe behavior or disclosure path;
- the Citus/PostgreSQL versions involved;
- a minimal redacted example;
- the safer expected behavior.

## Operational safety

Always test data movement, partition maintenance, topology changes, backup, restore, and upgrade procedures in a representative non-production environment. The repository is not a substitute for environment-specific access control, network security, HA, backup, or incident-response policies.
