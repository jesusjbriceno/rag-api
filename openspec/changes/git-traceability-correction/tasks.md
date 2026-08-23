# Tasks: Git Traceability Correction

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | 20–30 (two target docs, documentation-only) |
| 400-line budget risk | Low |
| Chained PRs recommended | No |
| Suggested split | Single PR |
| Delivery strategy | auto-chain |
| Chain strategy | pending (single PR, no chain needed) |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: pending
400-line budget risk: Low

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Correct traceability/rollback wording in both `architecture-boundaries` planning docs | PR 1 | `grep -nF` for canonical facts + `git diff --stat` ≤ 30 lines | N/A — documentation-only, no runtime boundary | Revert edits to `architecture-boundaries/proposal.md` + `tasks.md` only; `.proposals/07-decision-log.md` untouched |

## Phase 1: Edit `architecture-boundaries/proposal.md`

- [x] 1.1 Add two inventory rows to Affected Areas: self `proposal.md` (Correct affected-area inventory, rollback wording, traceability statement) and `tasks.md` (Correct rollback wording, add matching traceability statement). Keep existing spec-directory and Decision Log rows unchanged.
- [x] 1.2 Replace Rollback Plan wording with the canonical rollback contract; state no reachable history (unborn HEAD, no ref) distinct from the existing dangling commit `c9b40bfb`.
- [x] 1.3 Add `## Traceability Statement` stating the five canonical facts (unborn HEAD; dangling commit without Decision Log; Decision Log gitignored/untracked; D-003–D-006, D-101, D-102, D-115 verified accepted/unchanged; no seven-entry mutation proof, no history recovered).

## Phase 2: Edit `architecture-boundaries/tasks.md`

- [x] 2.1 Replace Rollback Plan with the canonical rollback contract (same no-reachable-history vs dangling-object distinction).
- [x] 2.2 Add matching `## Traceability Statement` with materially identical five facts; retain dated Correction Record and all completed `[x]` tasks unchanged.

## Phase 3: Invariant & Structural Verification

- [x] 3.1 Byte check: `sha256sum .proposals/07-decision-log.md` equals pre-edit hash; file byte-for-byte unchanged, zero edits.
- [x] 3.2 Invariant check: D-003, D-004, D-005, D-006, D-101, D-102, D-115 identifiers, statuses, and contents unchanged.
- [x] 3.3 Structural check: both target files contain one `## Traceability Statement` with identical canonical wording (five facts).
- [x] 3.4 Structural check: proposal Affected Areas has exactly the two new rows; existing rows unchanged.
- [x] 3.5 Forbidden-claim check: neither file states "no commits"/"no objects", exact mutation proof, or recovered history; advisory uses may/consider only.
- [x] 3.6 Rollback-limitation check: canonical rollback contract present in both files; "later commit is not a pre-change baseline" wording intact.

## Acceptance Criteria

- [x] Only the two target files changed; `.proposals/07-decision-log.md` unchanged.
- [x] Both files carry identical traceability wording and the accurate rollback limitation.
- [x] Changed lines ≤ 30; 400-line budget respected.
