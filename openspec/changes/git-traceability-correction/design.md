# Design: Git Traceability Correction

## Technical Approach

Apply a documentation-only correction to the two `architecture-boundaries` planning artifacts. Use one canonical factual statement in both files, add the missing self/tasks inventory rows, and replace rollback claims with restoration language bounded by an identified tracked pre-change baseline or an available external pre-change copy. No code, Git operation, policy, or accepted-decision content changes are included.

## Architecture Decisions

| Option | Tradeoff | Decision |
|---|---|---|
| One shared factual statement | Repeats text, but prevents semantic drift during review | Use materially identical traceability wording in both target files. |
| Minimal section-local edits | Leaves unrelated historical wording intact, but minimizes review scope | Edit only Affected Areas, Rollback Plan, and Traceability Statement/Correction Record sections. |
| Structural document checks | Provides no runtime test, but matches a repository with no test runner and a documentation-only boundary | Validate headings, table rows, required facts, forbidden claims, file allowlist, and unchanged decision content. |

## Data Flow

    Verified evidence and specification
                  │
                  v
       Canonical factual wording
            │             │
            v             v
       proposal.md     tasks.md
            └──── structural validation ────┘

## File Changes

| File | Action | Description |
|---|---|---|
| `openspec/changes/architecture-boundaries/proposal.md` | Modify | Add self and `tasks.md` inventory rows; replace rollback wording; add `## Traceability Statement`. |
| `openspec/changes/architecture-boundaries/tasks.md` | Modify | Replace rollback wording; add matching `## Traceability Statement`; retain the dated Correction Record and all completed tasks. |

## Interfaces / Contracts

The two files must state these facts without strengthening them:

1. HEAD is unborn, and no ref points to a commit containing `.proposals/07-decision-log.md`.
2. Git does contain a dangling commit, `c9b40bfb`, but that commit does not contain the Decision Log.
3. The Decision Log is gitignored and untracked.
4. The current contents of D-003, D-004, D-005, D-006, D-101, D-102, and D-115 were verified as accepted and remain unchanged by this correction.
5. Git cannot prove that the historical mutation changed only those seven entries; no history is claimed to have been recovered.

Use this rollback contract in both files:

> Restoring pre-change text requires either an identified tracked pre-change baseline containing that text or an available external pre-change copy. A later commit is not a pre-change baseline and cannot reconstruct an earlier untracked version. Neither source is identified in this workflow, so no reproducible rollback of the Decision Log edit is available.

The proposal Affected Areas table must include exactly these additional modified-artifact rows:

| Area | Impact | Description |
|---|---|---|
| `openspec/changes/architecture-boundaries/proposal.md` | Modified | Correct affected-area inventory, rollback wording, and traceability statement. |
| `openspec/changes/architecture-boundaries/tasks.md` | Modified | Correct rollback wording and add the matching traceability statement. |

The existing spec-directory and Decision Log rows remain unchanged.

## Invariants

- Only the two target files may change.
- `.proposals/07-decision-log.md` remains byte-for-byte unchanged.
- All seven decision identifiers, statuses, and contents remain unchanged.
- Existing completed task checkboxes and the dated Correction Record remain present.
- No statement says Git has no commits or objects, that exact mutation proof exists, or that history was recovered.
- Any future-edit advisory remains optional (`may`/`consider`) and establishes no policy or baseline requirement.

## Testing Strategy

| Layer | What to Test | Approach |
|---|---|---|
| Structural | Two-file scope, headings, and inventory rows | Inspect the change manifest; parse both Markdown files for one Traceability Statement and the two proposal table rows. |
| Content | Required facts and restoration boundary | Assert each fact and the canonical rollback contract appears in both files; reject the forbidden claims listed above. |
| Invariant | Seven decisions and completed task state | Compare the Decision Log bytes and task checkbox lines with their recorded pre-edit values. |

No runtime, unit, integration, or E2E tests apply. Keep the correction below 30 changed lines where practical and below the 400-line review budget.

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file classification, or process-integration boundary is designed.

## Migration / Rollout

No migration required. Publish both target-file corrections as one review unit; partial publication would leave contradictory traceability wording.

## Open Questions

None.
