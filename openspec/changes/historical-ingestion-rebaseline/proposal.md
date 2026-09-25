# Proposal: Historical Ingestion Rebaseline

Ingest a >500 GB local Windows document corpus into the RAG platform through a pausable, resumable, operator-controlled process — starting with an inventory and proof vertical slice before any scaling commitment.

## Quick path

1. Run a local inventory of eligible documents (count, size, format distribution) on the Windows source folders.
2. Benchmark extraction and embedding throughput on a representative sample.
3. Build a vertical slice: operator UI → inventory → process a small batch end-to-end with checkpointing.
4. Only after benchmark evidence confirms feasibility, scale to the full corpus across multiple nights.

## Context

The current ingestion pipeline is synchronous, single-document, 1 MiB-capped, and has no server-side progress tracking. The Companion CLI extracts multi-format documents locally but processes them sequentially with no checkpointing. The historical corpus exceeds 500 GB in local Windows folders, making the existing architecture fundamentally incompatible at this scale.

This proposal defines a rebaseline: a new ingestion pathway designed for bulk, long-running, operator-controlled historical document loading — independent of the existing real-time ingestion flow.

## Scope

### In scope

| Area | Deliverable |
| ------ | ------------- |
| **Inventory & benchmark** | Local tool that counts eligible documents, classifies by format/size, and benchmarks extraction + embedding throughput on a representative sample. Produces evidence before any scaling decision. |
| **Operator UI (local, non-technical)** | A desktop application (Electron candidate, not yet required) providing: document inventory view, start/pause/resume controls, processing state dashboard, audit log, and error viewer. Terminal is not the primary UX. |
| **Pausable/resumable ingestion** | Checkpoint-based processing that survives restarts, network failures, and multi-night runs. Each document's status (loaded / not loaded / error) is recorded durably. |
| **Error classification** | Network failures retry up to 3 times. Document errors (format, size, extraction failure) are classified, logged, and skipped without blocking subsequent documents. |
| **Legacy collection** | A searchable `legacy` collection in the RAG platform. Provenance and metadata are retained to permit later project classification without automatic taxonomy assumptions. |
| **Direct-auth API flow** | The loader authenticates directly to the data-plane API using client credentials + short-lived tokens. Plan the necessary API endpoints, scopes, token rotation/revocation, and secure local credential storage. |
| **AdminApp as control plane** | AdminApp creates/revokes API clients and secrets for the loader. It does NOT handle local file traversal or ingestion execution. |
| **Companion disposition** | Evaluate whether the existing Companion should be reused as a core/headless component, adapted, or treated as a reference implementation — based on benchmark evidence, not assumption. |

### Non-goals

| Non-goal | Rationale |
| ---------- | ----------- |
| Completing or depending on AdminApp WIP | AdminApp WIP has open blockers (security gate tests, Compose/CI). This change preserves and isolates it; integration is a separate future work unit. |
| Automatic taxonomy or project classification | Historical documents land in `legacy` with provenance metadata. Classification is a later concern. |
| GPU or external embedding services | CPU embedding via llama.cpp is the current baseline. Whether it meets volume/timing requirements is an open validation question, not a design assumption. |
| Modifying the existing real-time ingestion endpoint | The historical pathway is separate. Existing `/api/v1/collections/{id}/ingestions:txt` remains unchanged. |
| Deployment to production | This change authorizes planning only. Environments: local proof → OCI/Dokploy ARM64 staging → Coolify production. No deployment work in this phase. |
| Exposing PostgreSQL, llama.cpp, or internal services | These remain non-public. API access goes through Cloudflare Access/service tokens. |
| Creating branches, PRs, or CI pipelines | Delivery strategy is auto-chain, but this proposal phase creates no Git artifacts. |

## Product decisions (confirmed)

| Decision | Value |
| ---------- | ------- |
| Corpus size | >500 GB, primarily in local Windows folders |
| Primary UX | Non-technical operator-facing local UI (terminal is not the primary UX) |
| UI technology | Electron is a candidate, not yet required |
| Processing model | Pausable/resumable from confirmed checkpoints; may span many nights |
| Network failure policy | Retry at most 3 times |
| Document error policy | Classify, log, skip — never block later documents |
| Target collection | Searchable `legacy` collection with provenance/metadata |
| Loader authentication | Direct to API via client credentials + short-lived tokens |
| AdminApp role | Control plane for client/secret lifecycle only; no file traversal |
| AdminApp WIP | Preserve intact, isolate; dedicated clean work unit later for integration/Compose/CI |
| Environments | Local proof → OCI/Dokploy ARM64 staging → Coolify production (planning only) |
| Internal services | PostgreSQL, llama.cpp remain non-public; Cloudflare Access/service tokens for controlled API access |
| Delivery strategy | auto-chain (no branches/PRs in this phase) |

## Companion disposition (evidence-bounded)

The existing Companion CLI (`src/Rag.Companion/`) has verified capabilities: multi-format extraction (doc/docx/pdf/md/txt), normalized UTF-8 output, and direct API submission via service-client credentials. However, it is strictly sequential, has no checkpointing, no concurrency, and depends on a BFF that does not exist.

**This proposal does not assume Companion is the right foundation.** The first work unit (inventory + benchmark) must measure:

- Extraction throughput on a representative sample (documents/hour, GB/hour)
- LibreOffice sustained-batch reliability (memory pressure, hangs, timeout accumulation)
- Operator-machine resource headroom (disk, RAM) during extraction
- Embedding throughput via llama.cpp CPU on representative chunks

Based on benchmark evidence, the Companion disposition will be one of:

| Option | When |
| -------- | ------ |
| **Reuse as core/headless component** | If extraction adapters are reliable at scale and the sequential model can be extended with checkpointing and concurrency without fundamental rearchitecture. |
| **Adapt** | If extraction adapters are sound but the processing model (sequential, no checkpoint, BFF dependency) requires significant restructuring. Extract the adapter layer, rebuild the orchestration. |
| **Reference only** | If the Companion's architecture (sequential, BFF-dependent, win-x64 only) is too constraining. Use its adapter code as a reference for building a new extraction layer. |

No Companion code is modified during this proposal or the inventory/benchmark phase.

## AdminApp WIP disposition

| Principle | Detail |
| ----------- | -------- |
| **Preserve** | All existing AdminApp WIP code (`src/Rag.AdminApp.Host/`, `src/Rag.AdminApp/`, Dockerfile `admin` target) remains untouched. |
| **Isolate** | This change does not import, reference, or depend on any AdminApp WIP code. |
| **Control plane only** | AdminApp's future role is creating/revoking API clients and secrets for the loader. It does not execute ingestion or traverse local filesystems. |
| **Future integration** | A dedicated clean work unit will handle AdminApp integration, Compose service definition, and CI pipeline — only after this rebaseline's contractual boundaries are settled and the direct-auth API flow is proven. |

## First work unit: Inventory, benchmark, and proof vertical slice

The first real implementation work is NOT scaling to 500 GB. It is:

### 1. Inventory tool

- Scan the Windows source folders and produce a manifest: file count, total size, format distribution (doc/docx/pdf/md/txt), size distribution (histogram), and eligibility assessment (files that exceed extraction limits, unsupported formats, etc.).
- Output: structured report (JSON + human-readable summary) that informs all subsequent design decisions.

### 2. Benchmark on representative sample

- Select a statistically representative sample (by format and size) from the inventory.
- Measure: extraction time per document, normalized text size, embedding time per chunk, total throughput (GB/hour), resource consumption (RAM, disk I/O, CPU).
- Determine: whether CPU-only embedding can meet any reasonable timeline; whether LibreOffice sustains batch loads; whether the operator machine has sufficient headroom.
- Output: benchmark report with evidence-based recommendations for scaling strategy.

### 3. Proof vertical slice

- Build the minimum viable operator UI (inventory view + start/pause/resume + state dashboard).
- Implement checkpoint-based ingestion for a small batch (e.g., 100 documents) with full error classification and audit logging.
- Validate: pause/resume survives process restart; network failures retry correctly; document errors are skipped without blocking; checkpoint state is durable.
- Target: local environment with dev Compose.

**Only after these three steps produce evidence** does the proposal commit to a scaling strategy for the full 500 GB corpus.

## Unresolved technical validation

| Unknown | Why it matters | How to resolve |
| --------- | ---------------- | ---------------- |
| Actual file count and format distribution | Determines whether the problem is "many small files" or "few large files" — different solutions | Inventory tool (first work unit) |
| Extraction throughput at scale | Whether the Companion's extraction adapters can process 500 GB reliably and within an acceptable timeframe | Benchmark on representative sample |
| LibreOffice sustained-batch reliability | Memory pressure, hangs, timeout accumulation under continuous load | Benchmark with extended batch runs |
| CPU embedding throughput | Whether llama.cpp CPU-only can embed the corpus in days/weeks vs. months | Benchmark embedding time per chunk on representative sample |
| Operator machine resource headroom | Whether the Windows machine running the loader has sufficient RAM, disk, and CPU for sustained extraction | Benchmark + resource monitoring |
| Direct-auth API flow design | Endpoints, scopes, token rotation/revocation, secure local credential storage | Design phase after benchmark evidence |
| Checkpoint storage and format | Where and how checkpoint state is persisted (local SQLite? JSON? server-side?) | Design phase; must survive process restarts and multi-night runs |

## Acceptance outcomes

This proposal is successful when:

- [ ] An inventory tool produces a complete manifest of eligible documents from the Windows source folders.
- [ ] A benchmark report provides evidence-based answers for extraction throughput, embedding throughput, and resource requirements.
- [ ] A proof vertical slice demonstrates pausable/resumable ingestion with checkpointing on a small batch (≥100 documents).
- [ ] The operator UI provides inventory view, start/pause/resume controls, state dashboard, audit log, and error viewer.
- [ ] Error classification correctly retries network failures (≤3 times) and skips document errors without blocking.
- [ ] The `legacy` collection is searchable and retains provenance/metadata for each ingested document.
- [ ] The loader authenticates directly to the API using client credentials + short-lived tokens with secure local storage.
- [ ] AdminApp WIP remains untouched and isolated; no dependency on its completion.
- [ ] The Companion disposition decision (reuse/adapt/reference) is made based on benchmark evidence, not assumption.

## Risks

| Risk | Severity | Mitigation |
| ------ | ---------- | ------------ |
| CPU embedding throughput is too slow for 500 GB | High | Benchmark first; if evidence shows months-of-processing, the proposal must recommend GPU/external embedding or scope reduction before scaling. |
| LibreOffice unreliable under sustained batch load | High | Benchmark with extended runs; if hangs/timeouts are frequent, the proposal must recommend alternative extraction or per-batch cooldown strategies. |
| Operator machine lacks resource headroom | Medium | Benchmark includes resource monitoring; if insufficient, the proposal must recommend hardware upgrade or distributed extraction. |
| Direct-auth API flow introduces security surface | Medium | Design phase must address token rotation, revocation, secure local storage, and Cloudflare Access integration. No implementation before design review. |
| AdminApp WIP entanglement | Medium | Strict isolation: this change does not touch AdminApp code. Future integration is a separate work unit. |
| Checkpoint corruption or loss | Medium | Checkpoint design must include durability guarantees (write-ahead log, atomic updates, backup). Proof vertical slice validates checkpoint recovery. |
| 500 GB corpus contains unexpected format/size distribution | Medium | Inventory tool runs first; design adapts to actual evidence, not assumptions. |

## Rollback

- The inventory and benchmark tools are standalone; they do not modify the RAG platform or existing ingestion flow. Rollback: delete the tools.
- The proof vertical slice targets a separate `legacy` collection and does not affect existing collections. Rollback: delete the `legacy` collection.
- The operator UI is a local desktop application. Rollback: uninstall.
- No production data is modified until the proof vertical slice validates successfully.

## Success criteria

This change is complete when:

1. The inventory tool has scanned the full Windows corpus and produced a manifest.
2. The benchmark report provides evidence-based recommendations for scaling strategy.
3. The proof vertical slice has demonstrated pausable/resumable ingestion with checkpointing on a small batch.
4. The operator UI is functional and provides all required views and controls.
5. The `legacy` collection is searchable with provenance metadata.
6. The loader authenticates directly to the API with secure credential storage.
7. AdminApp WIP remains untouched.
8. The Companion disposition decision is documented with benchmark evidence.

Scaling to the full 500 GB corpus is a separate operational phase that begins only after this proposal's acceptance outcomes are met.
