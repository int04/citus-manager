#!/usr/bin/env python3
"""Validate the public citus-engineering skill package.

This script validates package structure and documentation hygiene. It does not
validate Citus SQL against a live database.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

REQUIRED = [
    "SKILL.md",
    "README.md",
    "LICENSE",
    "CHANGELOG.md",
    "CONTRIBUTING.md",
    "SECURITY.md",
    "CODE_OF_CONDUCT.md",
    ".github/workflows/validate.yml",
    ".github/ISSUE_TEMPLATE/bug_report.yml",
    ".github/ISSUE_TEMPLATE/documentation.yml",
    ".github/pull_request_template.md",
    "agents/openai.yaml",
    "references/01-architecture-and-capability-model.md",
    "references/02-data-modeling.md",
    "references/03-partitioning-and-time-series.md",
    "references/04-query-and-performance-optimization.md",
    "references/05-dml-transactions-and-ingestion.md",
    "references/06-columnar-and-hybrid-storage.md",
    "references/07-cluster-operations.md",
    "references/08-observability-security-ha-and-upgrades.md",
    "references/09-migrations-and-architecture-patterns.md",
    "references/10-troubleshooting.md",
    "references/11-command-reference.md",
    "references/12-decision-trees-and-checklists.md",
    "references/13-official-sources-and-version-policy.md",
    "references/14-advanced-sql-analytics-and-extensions.md",
    "scripts/README.md",
    "scripts/00-capability-scan.sql",
    "scripts/01-cluster-inventory.sql",
    "scripts/02-table-and-colocation-inventory.sql",
    "scripts/03-shard-skew-and-placement.sql",
    "scripts/04-partition-health.sql",
    "scripts/05-query-and-connection-diagnostics.sql",
    "scripts/06-topology-change-preflight.sql",
    "scripts/07-safe-auth-audit.sql",
    "scripts/capacity_model.py",
    "scripts/validate-package.py",
    "assets/architecture-review-template.md",
    "assets/design-decision-record-template.md",
    "assets/migration-runbook-template.md",
    "assets/performance-experiment-template.md",
    "assets/incident-report-template.md",
    "tests/skill-evaluation-prompts.md",
    "tests/test_capacity_model.py",
]

VIETNAMESE_CHARS = set(
    "\u0103\u00e2\u0111\u00ea\u00f4\u01a1\u01b0\u0102\u00c2\u0110\u00ca\u00d4"
    "\u01a0\u01af\u00e0\u00e1\u1ea3\u00e3\u1ea1\u1eb1\u1eaf\u1eb3\u1eb5\u1eb7"
    "\u1ea7\u1ea5\u1ea9\u1eab\u1ead\u00e8\u00e9\u1ebb\u1ebd\u1eb9\u1ec1\u1ebf"
    "\u1ec3\u1ec5\u1ec7\u00ec\u00ed\u1ec9\u0129\u1ecb\u00f2\u00f3\u1ecf\u00f5"
    "\u1ecd\u1ed3\u1ed1\u1ed5\u1ed7\u1ed9\u1edd\u1edb\u1edf\u1ee1\u1ee3\u00f9"
    "\u00fa\u1ee7\u0169\u1ee5\u1eeb\u1ee9\u1eed\u1eef\u1ef1\u1ef3\u00fd\u1ef7"
    "\u1ef9\u1ef5\u00c0\u00c1\u1ea2\u00c3\u1ea0\u1eb0\u1eae\u1eb2\u1eb4\u1eb6"
    "\u1ea6\u1ea4\u1ea8\u1eaa\u1eac\u00c8\u00c9\u1eba\u1ebc\u1eb8\u1ec0\u1ebe"
    "\u1ec2\u1ec4\u1ec6\u00cc\u00cd\u1ec8\u0128\u1eca\u00d2\u00d3\u1ece\u00d5"
    "\u1ecc\u1ed2\u1ed0\u1ed4\u1ed6\u1ed8\u1edc\u1eda\u1ede\u1ee0\u1ee2\u00d9"
    "\u00da\u1ee6\u0168\u1ee4\u1eea\u1ee8\u1eec\u1eee\u1ef0\u1ef2\u00dd\u1ef6"
    "\u1ef8\u1ef4"
)

MUTATING_SQL = re.compile(
    r"^\s*(INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE|CALL|DO|COPY|"
    r"VACUUM|ANALYZE|REINDEX|CLUSTER|GRANT|REVOKE)\b",
    re.IGNORECASE,
)

MARKDOWN_LINK = re.compile(r"\[[^\]]+\]\(([^)]+)\)")
TEXT_SUFFIXES = {".md", ".sql", ".yaml", ".yml", ".py"}


def fail(errors: list[str], message: str) -> None:
    errors.append(message)


def validate_required(errors: list[str]) -> None:
    if len(REQUIRED) != len(set(REQUIRED)):
        fail(errors, "The REQUIRED manifest contains duplicate paths")
    for rel in REQUIRED:
        path = ROOT / rel
        if not path.is_file():
            fail(errors, f"Missing required file: {rel}")


def iter_text_files():
    for path in ROOT.rglob("*"):
        if path.is_file() and path.suffix.lower() in TEXT_SUFFIXES:
            yield path


def validate_skill_frontmatter(errors: list[str]) -> None:
    path = ROOT / "SKILL.md"
    text = path.read_text(encoding="utf-8")
    match = re.match(r"\A---\n(.*?)\n---\n", text, re.DOTALL)
    if not match:
        fail(errors, "SKILL.md has no valid YAML frontmatter block")
        return
    frontmatter = match.group(1)
    if not re.search(r"^name:\s*citus-engineering\s*$", frontmatter, re.MULTILINE):
        fail(errors, "SKILL.md frontmatter must declare name: citus-engineering")
    description = re.search(r"^description:\s*(.+)$", frontmatter, re.MULTILINE)
    if not description or len(description.group(1).strip(" \"'")) < 80:
        fail(errors, "SKILL.md description is missing or too vague")


def validate_openai_yaml(errors: list[str]) -> None:
    text = (ROOT / "agents/openai.yaml").read_text(encoding="utf-8")
    for token in (
        "interface:",
        "display_name:",
        "short_description:",
        "default_prompt:",
        "policy:",
        "allow_implicit_invocation:",
    ):
        if token not in text:
            fail(errors, f"agents/openai.yaml is missing {token}")


def validate_internal_links(errors: list[str]) -> None:
    for path in ROOT.rglob("*.md"):
        text = path.read_text(encoding="utf-8")
        for target in MARKDOWN_LINK.findall(text):
            target = target.strip()
            if not target or target.startswith(("http://", "https://", "mailto:", "#")):
                continue
            target_path = target.split("#", 1)[0]
            if not target_path:
                continue
            resolved = (path.parent / target_path).resolve()
            try:
                resolved.relative_to(ROOT.resolve())
            except ValueError:
                fail(errors, f"Link escapes package root: {path.relative_to(ROOT)} -> {target}")
                continue
            if not resolved.exists():
                fail(errors, f"Broken link: {path.relative_to(ROOT)} -> {target}")


def validate_english(errors: list[str]) -> None:
    for path in iter_text_files():
        # The validator defines the detection alphabet itself; scanning this file
        # would therefore create a guaranteed false positive.
        if path.resolve() == Path(__file__).resolve():
            continue
        text = path.read_text(encoding="utf-8")
        found = sorted(set(text) & VIETNAMESE_CHARS)
        if found:
            fail(
                errors,
                f"Vietnamese-specific characters found in {path.relative_to(ROOT)}: {''.join(found)}",
            )


def validate_read_only_sql(errors: list[str]) -> None:
    for path in (ROOT / "scripts").glob("*.sql"):
        in_block_comment = False
        for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            line = raw.strip()
            if in_block_comment:
                if "*/" in line:
                    in_block_comment = False
                    line = line.split("*/", 1)[1].strip()
                else:
                    continue
            if line.startswith("/*"):
                if "*/" not in line[2:]:
                    in_block_comment = True
                    continue
                line = line.split("*/", 1)[1].strip()
            if not line or line.startswith(("--", "\\")):
                continue
            if MUTATING_SQL.match(line):
                fail(
                    errors,
                    f"Mutating SQL in read-only script {path.name}:{lineno}: {line}",
                )


def validate_no_secrets(errors: list[str]) -> None:
    patterns = [
        re.compile(r"password\s*=\s*['\"][^<'\"]+['\"]", re.IGNORECASE),
        re.compile(r"postgres(?:ql)?://[^\s<]+:[^\s<]+@", re.IGNORECASE),
        re.compile(r"BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY"),
    ]
    for path in iter_text_files():
        text = path.read_text(encoding="utf-8")
        for pattern in patterns:
            if pattern.search(text):
                fail(errors, f"Potential secret-like content in {path.relative_to(ROOT)}")


def validate_text_hygiene(errors: list[str]) -> None:
    for path in iter_text_files():
        text = path.read_text(encoding="utf-8")
        if not text.endswith("\n"):
            fail(errors, f"Missing final newline: {path.relative_to(ROOT)}")
        for lineno, line in enumerate(text.splitlines(), start=1):
            if line.endswith((" ", "\t")):
                fail(
                    errors,
                    f"Trailing whitespace: {path.relative_to(ROOT)}:{lineno}",
                )


def validate_markdown_fences(errors: list[str]) -> None:
    for path in ROOT.rglob("*.md"):
        fence_count = sum(
            1
            for line in path.read_text(encoding="utf-8").splitlines()
            if line.lstrip().startswith("```")
        )
        if fence_count % 2:
            fail(errors, f"Unbalanced Markdown code fences: {path.relative_to(ROOT)}")


def validate_python_syntax(errors: list[str]) -> None:
    for path in ROOT.rglob("*.py"):
        try:
            compile(path.read_text(encoding="utf-8"), str(path), "exec")
        except SyntaxError as exc:
            fail(
                errors,
                f"Python syntax error in {path.relative_to(ROOT)}:{exc.lineno}: {exc.msg}",
            )


def validate_size_and_structure(errors: list[str]) -> None:
    skill_lines = (ROOT / "SKILL.md").read_text(encoding="utf-8").count("\n") + 1
    if skill_lines > 500:
        fail(errors, f"SKILL.md is too large for focused progressive disclosure: {skill_lines} lines")
    reference_count = len(list((ROOT / "references").glob("*.md")))
    if reference_count < 10:
        fail(errors, "Expected at least 10 modular reference documents")


def main() -> int:
    errors: list[str] = []
    validate_required(errors)
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    validate_skill_frontmatter(errors)
    validate_openai_yaml(errors)
    validate_internal_links(errors)
    validate_english(errors)
    validate_read_only_sql(errors)
    validate_no_secrets(errors)
    validate_text_hygiene(errors)
    validate_markdown_fences(errors)
    validate_python_syntax(errors)
    validate_size_and_structure(errors)

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        print(f"Validation failed with {len(errors)} error(s).", file=sys.stderr)
        return 1

    files = [p for p in ROOT.rglob("*") if p.is_file()]
    lines = 0
    for path in files:
        if path.suffix.lower() in TEXT_SUFFIXES:
            lines += path.read_text(encoding="utf-8").count("\n") + 1
    print(f"Validation passed: {len(files)} files, {lines} text lines.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
