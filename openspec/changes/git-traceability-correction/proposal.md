# Proposal: Git Traceability Correction

## Intent

Correct factual inaccuracies in the `architecture-boundaries` planning artifacts regarding Git traceability and rollback. The current rollback sections conflate "no reachable commits" with "no Git objects," omit `tasks.md` from the affected-areas inventory, and lack an explicit traceability statement. This change adds factual corrections without altering any accepted decisions or claiming recovered history.

## Scope

### In Scope
- Add `tasks.md` row to the Affected Areas table in `openspec/changes/architecture-boundaries/proposal.md`
- Rewrite rollback sections in `proposal.md` and `tasks.md` to distinguish:
  - **No reachable history** (true: unborn HEAD, no ref points to a commit containing the decision log)
  - **No Git objects** (false: dangling commit `c9b40bfb` exists as a loose object with 12 files, but none contain the decision log)
- Add a factual traceability statement to both files:
  - Decision log is gitignored and untracked
  - Dangling commit `c9b40bfb` exists but does not contain the decision log
  - Seven accepted decision contents are correct per apply-progress verification
  - Exact mutation proof (seven-entry-only) is unavailable from Git
  - No history is claimed to have been recovered
- Add a non-binding advisory for future Decision Log edits (warning only, not a policy)

### Out of Scope
- Altering the seven accepted decision contents in `.proposals/07-decision-log.md`
- Modifying `design.md`, delta specs, or `architecture-validation/exploration.md`
- Git operations (no restore, no object manipulation)
- Establishing a baseline requirement or governance policy for future changes

## Capabilities

### New Capabilities
None (documentation-only correction, no spec-level behavior change)

### Modified Capabilities
None (no existing specs are affected)

## Approach

Minimal factual edits to two files. Use the abbreviated dangling-commit identifier `c9b40bfb` as evidence of a non-reachable Git object. Keep edits under 30 changed lines total. Verify against the Git state documented in the exploration.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `openspec/changes/architecture-boundaries/proposal.md` | Modified | Add `tasks.md` to Affected Areas; rewrite Rollback Plan; add Traceability Statement |
| `openspec/changes/architecture-boundaries/tasks.md` | Modified | Rewrite Rollback Plan; expand Correction Record with Traceability Statement |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Correction introduces new inaccuracies | Low | Keep edits minimal and factual; verify against Git state |
| Scope creep into decision content changes | Low | Explicit invariant list; verify step checks decision contents unchanged |
| Future sessions misinterpret traceability statement | Low | Use explicit, unambiguous language; avoid passive voice |

## Rollback Plan

Revert the edits to `proposal.md` and `tasks.md` using Git (once the repository has commits) or manual restoration from the pre-change versions. No decision contents are altered, so no rollback is needed for `.proposals/07-decision-log.md`.

**Advisory**: Future edits to `.proposals/07-decision-log.md` should consider adding the file to Git tracking or maintaining an external backup, since the current gitignored status prevents Git-based rollback.

## Dependencies

- Exploration artifact: `sdd/git-traceability-correction/explore` (Engram #350)
- User decision: `delivery/traceability-correction-scope` (Engram #351)
- Git state verification (2026-08-23): unborn HEAD, one dangling commit `c9b40bfb`, decision log gitignored/untracked

## Success Criteria

- [ ] `proposal.md` Affected Areas table includes `tasks.md` row
- [ ] `proposal.md` Rollback Plan distinguishes "no reachable history" from "no Git objects"
- [ ] `proposal.md` contains a factual traceability statement (decision log gitignored, dangling commit exists but does not contain decision log, seven decisions correct, no mutation proof, no recovered history claimed)
- [ ] `tasks.md` Rollback Plan distinguishes "no reachable history" from "no Git objects"
- [ ] `tasks.md` Correction Record includes matching traceability statement
- [ ] Seven accepted decision contents in `.proposals/07-decision-log.md` are unchanged
- [ ] Total changed lines < 400 (review budget)
