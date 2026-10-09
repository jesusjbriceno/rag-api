#!/usr/bin/env python3
"""Static guard: a workflow job that runs a repository script must check out the repository.

The offline test suites exercise script logic only; they cannot see workflow
wiring. That is how the ci-release retention job failed in production with
exit code 127: it called scripts/ci-prune-staging-versions.sh while the job
had no actions/checkout step, because it never needed one while the logic
was inline.

This check fails when a job's `run:` steps reference a repository file
under scripts/ while that same job has no actions/checkout step.

Usage:
    python3 scripts/validate-workflow-checkout.py [workflows-directory]

The directory defaults to .github/workflows at the repository root.
"""

import re
import sys
from pathlib import Path

import yaml

SCRIPT_REFERENCE = re.compile(r"\bscripts/[A-Za-z0-9_./-]+")
CHECKOUT_ACTION_PREFIX = "actions/checkout"
DEFAULT_WORKFLOWS_DIR = Path(__file__).resolve().parents[1] / ".github" / "workflows"


def find_violations(workflow_path):
    """Return (job_name, step_name, script_reference) violations in one workflow."""
    document = yaml.safe_load(workflow_path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        return []

    violations = []
    for job_name, job in (document.get("jobs") or {}).items():
        if not isinstance(job, dict):
            continue
        steps = job.get("steps") or []
        if any(
            isinstance(step, dict)
            and str(step.get("uses", "")).startswith(CHECKOUT_ACTION_PREFIX)
            for step in steps
        ):
            continue
        for step in steps:
            if not isinstance(step, dict):
                continue
            run = step.get("run")
            if not isinstance(run, str):
                continue
            match = SCRIPT_REFERENCE.search(run)
            if match:
                violations.append((str(job_name), str(step.get("name") or "<unnamed>"), match.group(0)))
    return violations


def main(argv):
    workflows_dir = Path(argv[1]) if len(argv) > 1 else DEFAULT_WORKFLOWS_DIR
    if not workflows_dir.is_dir():
        print(f"error: workflows directory not found: {workflows_dir}", file=sys.stderr)
        return 2

    workflow_paths = sorted(list(workflows_dir.glob("*.yml")) + list(workflows_dir.glob("*.yaml")))
    if not workflow_paths:
        print(f"error: no workflow files found in {workflows_dir}", file=sys.stderr)
        return 2

    failures = 0
    for workflow_path in workflow_paths:
        for job_name, step_name, script_reference in find_violations(workflow_path):
            failures += 1
            print(
                f"{workflow_path}: job '{job_name}' (step '{step_name}') runs repository "
                f"script '{script_reference}' but has no '{CHECKOUT_ACTION_PREFIX}' step"
            )

    if failures:
        print(f"validate-workflow-checkout: {failures} violation(s) found", file=sys.stderr)
        return 1
    print(f"validate-workflow-checkout: {len(workflow_paths)} workflow(s) checked, no violations")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
