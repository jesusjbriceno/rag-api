# Proposal: Architecture Boundaries

## Intent

Formalize the modular-monolith architecture, dependency direction, ingestion lifecycle, and early product decisions before any implementation. The repository contains 11 proposal documents (`.proposals/*.md`) that define a comprehensive design, but all architecture remains **proposed, not validated**. This change converts hypotheses into a formal specification that downstream phases (Fase 02–11) can rely on.

Three critical deferred decisions have been resolved: authentication mechanism (token exchange), deduplication rules (new document for unreferenced duplicates), and cross-collection search constraints (reject incompatible embedding profiles). These decisions must be encoded into the architecture specification.

## Scope

### In Scope
- Formal component diagram with 7 boundaries (API, Application, Domain, Processing, Embeddings, Persistence, Content Store)
- Module dependency rules (which modules depend on which, direction of dependencies)
- Ingestion lifecycle: Operation state machine (pending → running → succeeded/failed), pipeline stages (load → parse → normalize → chunk → embed → index), failure handling, manual retry semantics
- Authentication model: token exchange flow, credential lifecycle, HTTP contract integration points
- Content deduplication rules: hash-based logic, externalReference handling, DocumentVersion creation semantics
- Search compatibility constraints: cross-collection validation, embedding profile compatibility checks
- Stack decision justification: .NET/ASP.NET Core + PostgreSQL/pgvector rationale
- **Decision acceptance**: Move D-003 (PostgreSQL + pgvector), D-004 (.NET stack), D-005 (modular monolith), D-006 (local background worker), D-101 (authentication mechanism), D-102 (deduplication rules), and D-115 (multi embedding-profile search) from Proposed/Deferred → Accepted in the Decision Log

### Out of Scope
- Implementation code (this is documentation-only)
- Detailed error taxonomy (deferred to Fase 03 HTTP Contract)
- Idempotency window and storage mechanism (deferred to Fase 07)
- ContentReference physical format (deferred to Fase 05 Persistence)
- Vector indexing strategy (HNSW vs IVFFlat, deferred until load data exists)
- Delete semantics, metadata model, filter language, reranking, hybrid search, multi-tenancy, external queue/broker, OCR/vision providers, SDK generation, production Content Store (all remain Deferred)

## Capabilities

### New Capabilities
- `architecture-boundaries`: Component diagram, module responsibilities, dependency rules, stack justification
- `ingestion-lifecycle`: Operation state machine, background worker model, pipeline stages, failure handling, manual retry
- `product-decisions`: Authentication (token exchange), deduplication rules, search compatibility constraints

### Modified Capabilities
None (no existing specs)

## Approach

Document-first specification. Treat all `.proposals/*.md` content as hypotheses to be validated, not facts to be copied. Synthesize exploration findings (#332) and resolved user decisions (#333) into a coherent architecture specification. Use Given/When/Then scenarios for operation state transitions and deduplication rules. Explicitly mark remaining Deferred decisions and their resolution triggers.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `openspec/changes/architecture-boundaries/specs/architecture-boundaries/` | New | Formal architecture specification |
| `openspec/changes/architecture-boundaries/specs/ingestion-lifecycle/` | New | Operation state machine and pipeline |
| `openspec/changes/architecture-boundaries/specs/product-decisions/` | New | Resolved decisions (auth, dedup, search) |
| `.proposals/07-decision-log.md` | Modified | Update D-003, D-004, D-005, D-006 from Proposed → Accepted; update D-101, D-102, D-115 from Deferred → Accepted |
| `openspec/changes/architecture-boundaries/proposal.md` | Modified | Correct affected-area inventory, rollback wording, and traceability statement. |
| `openspec/changes/architecture-boundaries/tasks.md` | Modified | Correct rollback wording and add the matching traceability statement. |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Over-specification before implementation | Medium | Keep specs behavioral, not prescriptive; avoid implementation details |
| Decision premature commitment | Low | Explicitly document remaining Deferred decisions and resolution triggers |
| Architecture rigidity | Low | Modular monolith boundaries allow future extraction; specs focus on responsibilities, not structure |

## Rollback Plan

Delete `openspec/changes/architecture-boundaries/` (including its `specs/` delta files) to remove the specification artifacts; no code changes to revert. The Decision Log edit in `.proposals/07-decision-log.md` has no reproducible Git rollback. HEAD is unborn (`master`) and no ref points to a commit containing the Decision Log, so there is no reachable history for that file. Git does contain a dangling commit, `c9b40bfb`, but that commit does not contain the Decision Log, so it is not a pre-change baseline. The file is gitignored (`.gitignore` excludes `.proposals`) and untracked; restoring the original Decision Log text requires an external prior copy, which is unavailable in this workflow.

Restoring pre-change text requires either an identified tracked pre-change baseline containing that text or an available external pre-change copy. A later commit is not a pre-change baseline and cannot reconstruct an earlier untracked version. Neither source is identified in this workflow, so no reproducible rollback of the Decision Log edit is available.

**Advisory**: Future edits to `.proposals/07-decision-log.md` may consider adding the file to Git tracking or maintaining an external backup, since the current gitignored status prevents Git-based rollback. This advisory is non-binding and establishes no policy or baseline requirement.

## Traceability Statement

- HEAD is unborn, and no ref points to a commit containing `.proposals/07-decision-log.md`.
- Git does contain a dangling commit, `c9b40bfb`, but that commit does not contain the Decision Log.
- The Decision Log is gitignored and untracked.
- The current contents of D-003, D-004, D-005, D-006, D-101, D-102, and D-115 were verified as accepted and remain unchanged by this correction.
- Git cannot prove that the historical mutation changed only those seven entries; no history is claimed to have been recovered.

## Dependencies

- Exploration artifact: `sdd/explore/architecture-validation` (Engram #332)
- User decisions: `architecture/fase-01-product-decisions` (Engram #333)
- Foundational context: `.proposals/*.md` (11 documents)

## Success Criteria

- [ ] Component diagram with 7 boundaries and dependency rules documented
- [ ] Operation state machine with valid transitions specified (pending → running → succeeded/failed, manual retry)
- [ ] Authentication model (token exchange) integrated into HTTP contract design points
- [ ] Deduplication rules specified (same externalReference + same hash → idempotent; same externalReference + different hash → new version; no externalReference → new document)
- [ ] Search compatibility constraints documented (reject cross-collection search with incompatible embedding profiles)
- [ ] Decision Log updated: D-003, D-004, D-005, D-006 moved from Proposed → Accepted; D-101, D-102, D-115 moved from Deferred → Accepted
- [ ] Remaining Deferred decisions explicitly listed with resolution triggers
- [ ] Ready for Fase 02 (Domain Model & Document Lifecycle)
