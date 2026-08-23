# Tasks: Architecture Boundaries

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | 30–50 (decision-log edits only) |
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
| 1 | Mark D-003–D-006 and D-101, D-102, D-115 `Accepted` in decision log | PR 1 | `grep -nE 'D-003|D-004|D-005|D-006|D-101|D-102|D-115' .proposals/07-decision-log.md` | N/A — documentation-only, no runtime boundary | No Git rollback — file ignored, untracked, no reachable commit contains it; pre-change text requires an external prior copy (unavailable) |

## Phase 1: Accepted Baseline (D-003–D-006)

- [x] 1.1 In `.proposals/07-decision-log.md`, set D-003 "PostgreSQL + pgvector" to `Estado: Accepted` (keep existing `Decisión`). (~2 lines)
- [x] 1.2 Same file: set D-004 "Stack principal .NET" to `Estado: Accepted`; change "Decisión propuesta:" → "Decisión:". (~3 lines)
- [x] 1.3 Same file: set D-005 "Modular monolith first" to `Estado: Accepted`. (~2 lines)
- [x] 1.4 Same file: set D-006 "Background worker local primero" to `Estado: Accepted`. (~2 lines)

## Phase 2: Resolved Deferred (D-101, D-102, D-115)

- [x] 2.1 Same file: set D-101 "Authentication mechanism" to `Estado: Accepted`; record `Decisión: token exchange (KeyId + secret → short-lived token); verified identity`. Keep token format/headers, hashing, scopes deferred to Fase 04. (~6 lines)
- [x] 2.2 Same file: set D-102 "Deduplication rules" to `Estado: Accepted`; record same externalReference + same hash → existing document/version; changed hash → new DocumentVersion; no externalReference → new Document (across collections). (~6 lines)
- [x] 2.3 Same file: set D-115 "Multi embedding-profile search" to `Estado: Accepted`; record rejection of cross-collection search with incompatible embedding profiles. (~5 lines)

## Phase 3: Validation (depends on Phases 1–2)

- [x] 3.1 Confirm exactly seven entries flipped: D-003–D-006 `Accepted`; D-101, D-102, D-115 `Accepted`.
- [x] 3.2 Confirm remaining `Proposed` entries (D-007, D-009) unchanged; remaining `Deferred` entries (D-103–D-114, D-116) unchanged.
- [x] 3.3 Confirm no source or test files created; repository remains documentation-only.
- [x] 3.4 Confirm edits are in Spanish and match product-decisions spec + design data flow.

## Acceptance Criteria

- [x] D-003, D-004, D-005, D-006 are `Accepted`; D-101, D-102, D-115 are `Accepted`; all other entries unchanged.
- [x] Decision-log text matches the product-decisions delta spec (token exchange, dedup, compatibility) and the design data flow.
- [x] Spanish Decision Log language preserved; no implementation code added; no other Deferred decision resolved.

## Rollback Plan

No reproducible Git rollback exists for the Decision Log edit. HEAD is unborn (`master`) and no ref points to a commit containing `.proposals/07-decision-log.md`, so there is no reachable history for that file. Git does contain a dangling commit, `c9b40bfb`, but that commit does not contain the Decision Log, so it is not a pre-change baseline. The file is gitignored (`.gitignore` excludes `.proposals`) and untracked; restoring the seven entries to their prior Proposed/Deferred state requires an external prior copy, which is unavailable in this workflow. No code to revert.

Restoring pre-change text requires either an identified tracked pre-change baseline containing that text or an available external pre-change copy. A later commit is not a pre-change baseline and cannot reconstruct an earlier untracked version. Neither source is identified in this workflow, so no reproducible rollback of the Decision Log edit is available.

## Traceability Statement

- HEAD is unborn, and no ref points to a commit containing `.proposals/07-decision-log.md`.
- Git does contain a dangling commit, `c9b40bfb`, but that commit does not contain the Decision Log.
- The Decision Log is gitignored and untracked.
- The current contents of D-003, D-004, D-005, D-006, D-101, D-102, and D-115 were verified as accepted and remain unchanged by this correction.
- Git cannot prove that the historical mutation changed only those seven entries; no history is claimed to have been recovered.

## Correction Record

**2026-08-23** — Corrective remediation. The previous `git restore .proposals/07-decision-log.md` rollback boundary was inaccurate and has been removed: the Decision Log is gitignored, untracked, and no reachable commit contains it, so no Git recovery of the pre-change text is possible. Rollback is limited to manual restoration from an external prior copy, which is unavailable in this workflow. All completed decision-log tasks (1.1–3.4) and their `[x]` marks are preserved unchanged; no accepted decision or scope was altered.

## Dependencies

Phase 2 and Phase 3 depend on Phase 1. No code, runtime, or test-infra dependencies (project has none).
