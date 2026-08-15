## Problem

Describe the correctness, safety, coverage, or clarity issue.

## Change

Describe the files and skill behavior changed.

## Compatibility evidence

- PostgreSQL version(s):
- Citus version(s):
- Deployment/provider:
- Runtime signatures/GUCs/views checked:
- Official sources:

## Risk and safety

- Risk class affected: `READ` / `SESSION` / `WRITE` / `IMPACT` / `DESTRUCTIVE`
- New failure modes:
- Validation and rollback guidance:
- Secret/private-data review completed: yes/no

## Validation

- [ ] `python3 scripts/validate-package.py` passes.
- [ ] Relevant prompts in `tests/skill-evaluation-prompts.md` were reviewed or updated.
- [ ] New commands identify node/database, privileges, capability checks, and verification.
- [ ] No project-specific hosts, schemas, credentials, or fixed versions were introduced.
