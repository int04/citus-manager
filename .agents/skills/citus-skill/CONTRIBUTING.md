# Contributing

Contributions that improve accuracy, safety, version-awareness, or clarity are welcome.

## Principles

- Prefer official Citus, PostgreSQL, and OpenAI documentation.
- Keep commands version-aware; avoid claiming a function or signature exists everywhere.
- Keep `SKILL.md` focused and route deep detail into `references/`.
- Use infrastructure-neutral placeholders.
- Mark operational risk and include validation plus rollback or recovery guidance.
- Never include real credentials, private hostnames, customer schemas, or production data.
- Keep SQL under `scripts/` read-only unless the filename and documentation explicitly state otherwise.

## Proposed changes

A pull request should explain:

1. the problem being solved;
2. the Citus/PostgreSQL versions checked;
3. official sources used;
4. expected skill behavior before and after the change;
5. any new risks or compatibility limits;
6. validation performed.

## Documentation style

- Write in clear technical English.
- Use imperative instructions in `SKILL.md`.
- Distinguish facts, heuristics, examples, and version-sensitive behavior.
- Use `<PLACEHOLDER>` notation for project-specific values.
- Do not turn benchmark starting points into universal defaults.
- Cross-link instead of duplicating large sections.

## Validation

Before submitting:

```bash
python3 scripts/validate-package.py
python3 -m unittest discover -s tests -p 'test_*.py'
```

Also review the prompts in `tests/skill-evaluation-prompts.md` and confirm that the skill does not recommend unsafe production command chains. Structural validation and deterministic calculator tests run in GitHub Actions for pushes and pull requests.
