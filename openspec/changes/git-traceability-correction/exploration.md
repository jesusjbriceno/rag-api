# Exploration: Git Traceability Correction

## Purpose

Repair inaccurate traceability and rollback claims in the `architecture-boundaries` planning artifacts after its apply phase exhausted its own correction budget. This exploration documents the verified Git state, identifies the specific inaccuracies, and proposes a documentation-only, non-destructive correction scope for a new, separate SDD change named `git-traceability-correction`.

## Current State

### Git Repository State (verified 2026-08-23)

| Fact | Value |
|---|---|
| HEAD | Unborn branch `master` (no commits) |
| Refs containing commits | None |
| Dangling commits | One: `c9b40bfb270899ec8c37ceb769b30862e9655fe3` |
| Dangling commit message | `docs: init repo with SDD starter pack context and openspec baseline` |
| Dangling commit tree | 12 files (`.gitignore`, `README.md`, `openspec/changes/architecture-boundaries/{design.md, proposal.md}`, `openspec/changes/architecture-boundaries/specs/{architecture-boundaries,ingestion-lifecycle,product-decisions}/spec.md`, `openspec/changes/architecture-validation/exploration.md`, `openspec/config.yaml`, two `.gitkeep` files) |
| `.proposals/07-decision-log.md` in dangling commit | No (gitignored via `.gitignore` rule `.proposals`) |
| `tasks.md` in dangling commit | No (created after the dangling commit) |
| Reflog | Empty (no commits on current branch) |
| Stash | Empty |
| Loose objects | 21 |

### Decision Log State (verified on disk)

| Fact | Value |
|---|---|
| File | `.proposals/07-decision-log.md` |
| Git tracking | Untracked, gitignored |
| Total entries | 30 (D-001 through D-014, D-101 through D-116) |
| Entries flipped to Accepted by `architecture-boundaries` apply | 7: D-003, D-004, D-005, D-006, D-101, D-102, D-115 |
| Remaining Proposed | 2: D-007, D-009 |
| Remaining Deferred | 12: D-103 through D-114, D-116 |
| Pre-change text recoverable from Git | No |
| Exact mutation proof (seven-entry-only) available | No |
| Current seven decision contents correct | Yes (per apply-progress record #347 and grep verification) |

### Planning Artifacts State

| Artifact | In dangling commit | On disk | Differs from dangling commit |
|---|---|---|---|
| `openspec/changes/architecture-boundaries/proposal.md` | Yes | Yes | Yes — affected areas paths corrected (`.proposals/01-decision-log.md` to `.proposals/07-decision-log.md`; `openspec/specs/...` to `openspec/changes/architecture-boundaries/specs/...`); rollback plan corrected to manual-recovery limitation; D-003 through D-006 added to scope |
| `openspec/changes/architecture-boundaries/design.md` | Yes | Yes | Yes — rewritten to align with accepted D-003 through D-006 baseline; implementation-specific paths removed; contracts and data flow adjusted |
| `openspec/changes/architecture-boundaries/tasks.md` | No | Yes | N/A (post-commit creation); contains Correction Record acknowledging rollback limitation |
| `openspec/changes/architecture-boundaries/specs/*/spec.md` | Yes | Yes | Not verified in this exploration (out of scope) |
| `openspec/changes/architecture-validation/exploration.md` | Yes | Yes | Not verified in this exploration (out of scope) |

### Inaccuracies Identified

1. **`proposal.md` Affected Areas table omits `tasks.md`**: The proposal lists `proposal.md`, spec paths, and `.proposals/07-decision-log.md` as affected areas, but `tasks.md` was also modified during apply (14 checkboxes marked `[x]`, Correction Record added). This omission means the affected-areas inventory is incomplete.

2. **Rollback sections conflate "no reachable history" with "no Git objects/commits"**: The corrected rollback sections in `proposal.md` and `tasks.md` state that the pre-change decision log text "is not recoverable from Git" and that "the repository has no commits." While factually correct that there are no reachable commits, the phrasing does not distinguish between:
   - **No reachable history**: No branch, tag, or reflog entry points to a commit containing the decision log. This is true.
   - **No Git objects/commits at all**: The repository has zero commits in its object store. This is FALSE — the dangling commit `c9b40bfb` exists as a loose object and contains 12 files (just not the decision log).

   The distinction matters: the dangling commit proves Git objects exist; the inaccuracy is that none of those objects contain the decision log or any post-init mutation of it.

3. **No explicit traceability statement**: Neither `proposal.md` nor `tasks.md` contains a factual statement about what can and cannot be proven from Git regarding the seven-entry mutation. The Correction Record in `tasks.md` acknowledges the limitation but does not provide a complete traceability account.

4. **Historical proof of exact seven-entry-only mutation is unavailable**: The seven decision contents are currently correct (verified by grep and apply-progress record), but there is no Git-based proof that exactly seven entries were mutated and nothing else. The decision log is gitignored, so Git never tracked it. The dangling commit predates the apply and does not contain the file at all. No external prior copy exists in this workflow.

## Affected Areas

- `openspec/changes/architecture-boundaries/proposal.md` — Affected Areas table must add `tasks.md`; Rollback Plan must distinguish "no reachable history" from "no Git objects/commits"; needs traceability statement.
- `openspec/changes/architecture-boundaries/tasks.md` — Rollback Plan must distinguish "no reachable history" from "no Git objects/commits"; Correction Record should be expanded or a Traceability Statement section added.
- `openspec/changes/git-traceability-correction/` — New change folder (this exploration, plus subsequent proposal/specs/design/tasks if approved).

### NOT Affected (explicitly out of scope)

- `.proposals/07-decision-log.md` — The seven accepted decision contents are correct and must NOT be altered.
- `openspec/changes/architecture-boundaries/design.md` — Already corrected; no traceability inaccuracies identified.
- `openspec/changes/architecture-boundaries/specs/*/spec.md` — Out of scope for this correction.
- `openspec/changes/architecture-validation/exploration.md` — Out of scope.
- Git object store — No destructive or restorative Git operations. The dangling commit remains as-is.

## Approaches

### 1. Amend existing `architecture-boundaries` artifacts in place

Add corrections directly to `proposal.md` and `tasks.md` without a new SDD change.

- Pros: Minimal overhead; fixes are small (a few lines per file).
- Cons: Blurs the audit trail of the completed `architecture-boundaries` change; no formal scope/rollback for the correction itself; mixes correction with completed work.
- Effort: Low

### 2. New SDD change `git-traceability-correction` (recommended)

Create a separate SDD change with its own proposal, specs, design, and tasks. The correction targets are the two `architecture-boundaries` planning artifacts (`proposal.md`, `tasks.md`). The change has its own rollback plan and success criteria.

- Pros: Clean audit trail; proper SDD workflow; clear scope boundary; correction is reviewable and verifiable on its own terms; does not alter decision contents.
- Cons: Slightly more overhead (one proposal, one spec, one design, one tasks file); but all are documentation-only and small.
- Effort: Low

### 3. Standalone traceability document outside SDD

Create a single `traceability-correction.md` file in the `architecture-boundaries` folder or project root, outside the SDD workflow.

- Pros: Simple; one file.
- Cons: No workflow structure; no verification step; bypasses the SDD pipeline that the project already uses; harder to track in future sessions.
- Effort: Low

## Recommendation

**Approach 2: New SDD change `git-traceability-correction`**.

Rationale:
- The project already uses the SDD workflow (openspec + engram hybrid). Bypassing it for a correction would create an inconsistency.
- A separate change provides a clean audit trail: the `architecture-boundaries` change remains complete with its known limitations; the `git-traceability-correction` change explicitly addresses those limitations.
- The correction is small (documentation-only, ~20-30 changed lines across two target files), well within the 400-line review budget.
- The change can be verified independently: success criteria are factual statements about Git state, not behavioral claims.

### Proposed Correction Scope

The `git-traceability-correction` change should make these specific edits:

#### Target 1: `openspec/changes/architecture-boundaries/proposal.md`

1. **Affected Areas table**: Add `tasks.md` row:
   ```
   | `openspec/changes/architecture-boundaries/tasks.md` | Modified | 14 task checkboxes marked `[x]`; Correction Record added documenting rollback limitation. |
   ```

2. **Rollback Plan**: Replace the current rollback text with a version that explicitly distinguishes:
   - "No reachable history" (true: no ref points to a commit containing the decision log)
   - "No Git objects/commits" (false: the dangling commit `c9b40bfb` exists as a loose object with 12 files, but none contain the decision log)

3. **Traceability Statement** (new section or paragraph): Add a factual statement:
   - The decision log is gitignored and untracked.
   - The dangling commit `c9b40bfb` exists but does not contain the decision log.
   - The seven accepted decision contents are correct per apply-progress verification.
   - Exact mutation proof (seven-entry-only) is unavailable from Git.
   - No history is claimed to have been recovered.

#### Target 2: `openspec/changes/architecture-boundaries/tasks.md`

1. **Rollback Plan**: Same distinction as Target 1.2 — replace "the repository has no commits" with "no reachable commit contains the decision log; a dangling commit exists but does not track this file."

2. **Correction Record**: Expand or add a Traceability Statement that matches Target 1.3.

#### Invariants (must NOT change)

- The seven accepted decision contents in `.proposals/07-decision-log.md`.
- The dangling commit or any Git object.
- Any source, test, or implementation file (none exist).
- The `design.md` content (already corrected).
- The delta spec contents (out of scope).

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Correction introduces new inaccuracies | Low | Keep edits minimal and factual; verify against Git state before and after |
| Future sessions misinterpret the traceability statement | Low | Use explicit, unambiguous language; avoid passive voice |
| Correction scope creeps into decision content changes | Low | Explicit invariant list; verify step checks decision contents are unchanged |
| Dangling commit is garbage-collected, losing the openspec baseline | Low | Not relevant to this correction; the baseline is already on disk |

## Ready for Proposal

**Yes**. The exploration is complete. The orchestrator should inform the user that:

1. The Git state is verified: unborn HEAD, one dangling commit (no decision log), no refs, decision log is gitignored/untracked.
2. Three specific inaccuracies were identified in `proposal.md` and `tasks.md`: missing `tasks.md` in affected areas, conflation of "no reachable history" with "no Git objects/commits", and absence of an explicit traceability statement.
3. The seven accepted decision contents are correct and will NOT be altered.
4. The recommended approach is a new SDD change `git-traceability-correction` with documentation-only edits to `proposal.md` and `tasks.md`.
5. The correction is small (~20-30 lines), well within the 400-line review budget, and verifiable.
