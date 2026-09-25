# Exploration: Historical Ingestion Rebaseline

## Summary

The current ingestion pipeline accepts only TXT JSON payloads with a hard 1 MiB request/normalization limit. The Companion CLI handles multi-format extraction (doc/docx/pdf/md/txt) locally and submits through the same TXT-only endpoint, but processes files sequentially with no batching, streaming, or server-side progress tracking. The historical corpus exceeds 500 GB, which is fundamentally incompatible with the current per-document, per-request, synchronous architecture. This exploration maps the actual capabilities, WIP boundaries, and the decomposition required before a proposal can be ready.

## Current Ingestion Architecture (verified from source)

### Data-plane API (`src/Rag.Api/Program.cs`)

| Constraint | Value | Source |
| --- | --- | --- |
| Max request body | 1 MiB (`MaxIngestionBodyBytes = 1_048_576`) | `Program.cs:277` |
| Endpoint | `POST /api/v1/collections/{id}/ingestions:txt` | `Program.cs:160` |
| Content type | UTF-8 JSON only | `ApiEndpointSupport.ReadJsonAsync` |
| Chunking | 2,000 chars per chunk, 200 overlap | `TxtChunker.cs` |
| Deduplication | SHA-256 of normalized text as `external_reference` | `AcceptTxtIngestion.cs` |

**Key observation**: The API accepts a single text document per request, synchronously creates an operation, and the caller polls to terminal state. There is no batch endpoint, no streaming ingestion, and no server-side bulk progress tracking.

### Companion CLI (`src/Rag.Companion/`)

| Capability | Status | Evidence |
| --- | --- | --- |
| Multi-format extraction | ✅ doc/docx/pdf/md/txt | `AdapterRegistry.CreateDefault` |
| Normalized UTF-8 output | ✅ with 1 MiB cap | `AdapterRegistry.MaxNormalizedUtf8Bytes` |
| BFF lease protocol | ✅ client-side code exists | `CompanionHttpClient.cs` |
| BFF server endpoint | ❌ No deployed BFF | AdminApp WIP incomplete |
| Sequential processing | ✅ one file at a time | `CompanionRun.RunAsync` foreach loop |
| Parallel/concurrent ingestion | ❌ Not implemented | No `Parallel.ForEach`, no concurrency |
| Resumption after failure | ❌ Per-file retry only | `RetryPolicy` with 3 attempts; no checkpoint |
| Corpus progress tracking | ❌ Local counters only | `processed`/`failed` in `CompanionRun` |

### Companion → API connection (verified gap)

The `IngestionClient` submits directly to the data-plane API using service-client credentials (token exchange → bearer token → POST). The BFF companion protocol (`CompanionHttpClient`) handles lease acquisition and event reporting separately. **There is no deployed BFF server** — the AdminApp WIP contains the BFF host scaffold (`src/Rag.AdminApp.Host/`) but it has open blockers (security gate tests missing, Docker `admin` target was absent in prior apply, now present in Dockerfile but not in Compose/CI).

## AdminApp WIP Boundary

### What exists on disk (dirty working tree)

| Component | Location | State |
| --- | --- | --- |
| BFF Host | `src/Rag.AdminApp.Host/` | Scaffold with Cloudflare JWT, assertion issuance, HMAC signing, API proxy |
| AdminApp library | `src/Rag.AdminApp/` | Assertion issuer, Cloudflare Access options |
| Dockerfile `admin` target | `Dockerfile:59` | Present (builds `Rag.AdminApp.Host.dll`) |
| Compose service | Not in `compose.coolify.yaml` | ❌ Absent |
| CI pipeline | Not in `.github/workflows/` | ❌ Absent |
| Integration tests | `adminapp-deployable-dashboard/apply-progress.md` | Open blockers: security gate + health tests missing |

### Recommendation: preserve + isolate

The AdminApp WIP is a separate concern (admin UI dashboard) that happens to contain the BFF companion protocol server. It must be preserved intact and isolated in its own clean work unit. This historical-ingestion change must not depend on, modify, or unblock AdminApp WIP. If the historical ingestion needs a BFF, it must either:

1. Define its own minimal BFF endpoint independent of AdminApp, or
2. Explicitly sequence after AdminApp WIP is complete and deployed.

## The >500 GB Problem Decomposition

### Why the current architecture fails at scale

| Bottleneck | Current limit | Impact at 500 GB |
| --- | --- | --- |
| Per-document size | 1 MiB normalized text | Large PDFs/books fail extraction |
| Per-request model | Synchronous POST → operation → poll | ~500K+ requests for 500 GB at 1 KB avg; sequential = weeks |
| No batching | One document per HTTP call | No throughput optimization possible |
| No server-side progress | Client tracks processed/failed | No resumability, no operator visibility |
| No corpus manifest | Companion scans filesystem | No pre-flight estimation, no skip-already-ingested at scale |
| Single embedding model | Qwen3-Embedding-0.6B CPU-only | Embedding throughput is the actual bottleneck, not HTTP |

### Decomposition axes

1. **Extraction layer**: The Companion has local adapters for all five formats (doc/docx/pdf/md/txt) and processes each file through a copy-to-snapshot → extract → normalize → submit pipeline. However, the repository provides no throughput benchmarks, no reliability data at scale, and no evidence about LibreOffice behaviour under sustained batch loads (memory pressure, hangs, or timeout accumulation). The CLI is strictly sequential (`foreach` in `CompanionRun.RunAsync`), imposes a 1 MiB normalized-text cap per document, and enforces a 60-second timeout per LibreOffice invocation. **Whether the extraction layer can process 500 GB reliably, within an acceptable timeframe, or without operator-machine resource exhaustion is an unresolved validation risk** — not a confirmed capability. Extraction capacity at this scale requires benchmarking before any design can assume it as a non-bottleneck.

2. **Submission layer**: The 1 MiB per-document limit and synchronous model are the primary blockers. Options:
   - **Batch ingestion endpoint**: Accept N documents per request (server-side fan-out to operations).
   - **Streaming/chunked ingestion**: Accept large documents as streamed bodies, server-side chunking.
   - **File-based ingestion**: Upload to object storage, server-side async processing with progress API.
   - **Increased limits**: Raise the 1 MiB cap (simplest but doesn't solve throughput).

3. **Progress/resumption layer**: Server-side corpus tracking (what's been ingested, what failed, what's in-progress) with a progress query API.

4. **Embedding throughput**: llama.cpp CPU-only embedding is the physical bottleneck. At ~600 MB model, CPU embedding of 500 GB of text chunks is days-to-weeks regardless of ingestion pipeline improvements.

## Security and Network Topology (verified from Compose)

### Current exposure

| Service | Ports | Network | Exposure |
| --- | --- | --- | --- |
| `api` | None in Coolify Compose; `127.0.0.1:8080` in dev | Docker default | Coolify reverse proxy only |
| `postgres` | None | Docker internal | Not exposed |
| `llama-cpp` | None | Docker internal | Not exposed |
| `model-download` | None | Docker internal | Init container, exits |

### Access requirements for historical ingestion

- **Companion CLI** needs to reach the data-plane API (`POST /api/v1/collections/{id}/ingestions:txt`) — currently requires service-client credentials and a reachable API URL.
- **Local tools/Hermes** need controlled API access — the API is behind Coolify/Cloudflare; local access requires either a tunnel, a direct internal route, or a local dev Compose instance.
- **PostgreSQL, llama-cpp** must NOT be exposed — confirmed they are internal-only in both Compose files.

### Staging vs. production decision points

| Decision | Current state | Impact on historical ingestion |
| --- | --- | --- |
| Coolify = production? | Intended but requires reconciliation | Ingestion must target the correct environment |
| OCI/Dokploy = staging? | Integration/staging history | May be the safe target for initial bulk ingestion |
| Dev Compose (`compose.dev.yaml`) | Local only, API + migrate | Could run a local ingestion target for testing |

## Obsolete Planning Assumptions (identified)

1. **"Companion handles the historical import end-to-end"** — The Companion was designed for a one-time snapshot of a personal document collection, not 500 GB. Its sequential processing, local-only progress tracking, and dependency on a BFF that doesn't exist make this assumption obsolete.

2. **"The BFF companion protocol is ready"** — The client code exists but no server implements it. The AdminApp WIP has open blockers.

3. **"1 MiB per document is sufficient"** — This was designed for typical office documents. Historical corpus may contain large scanned PDFs, books, or multi-MB text extractions that exceed this limit.

4. **"Synchronous ingestion is acceptable"** — For a few hundred documents, yes. For 500 GB, the synchronous poll-per-document model creates unacceptable coupling and no operator visibility.

## Unresolved Product Decisions (must block proposal readiness)

These decisions have no repository evidence and require explicit product input before a proposal can be written:

1. **What is the actual corpus size and file count?** "Over 500 GB" is a lower bound. The number of files, average file size, and format distribution determine whether the problem is "many small files" or "few large files" — these require different solutions.

2. **Where does the corpus live?** Dropbox-synced local folders (Companion assumption)? S3? A NAS? The extraction location determines whether the Companion model (local extraction) or a server-side model (remote extraction) is appropriate.

3. **What is the acceptable ingestion timeline?** Days? Weeks? Months? This determines whether CPU-only embedding is acceptable or whether GPU/external embedding services are needed.

4. **Is the Companion's Windows-only constraint acceptable?** The current Companion is `win-x64` only. If the corpus is on a Linux server, a new extraction tool is needed.

5. **What is the rollback/re-ingestion strategy for 500 GB?** The current deduplication by `external_reference` is idempotent, but a failed 500 GB run with no server-side checkpoint means restarting from scratch or building a custom resume mechanism.

6. **Should historical ingestion use the same collection(s) as ongoing ingestion, or separate collections?** This affects deduplication, access control, and retrieval quality.

7. **Is the AdminApp BFF the intended companion protocol server, or should historical ingestion define its own minimal protocol?** This is an architectural sequencing decision.

## Recommended Planning Sequence

1. **Resolve product decisions above** — These are genuine blockers, not technical unknowns that exploration can answer.
2. **Decompose into independent changes**:
   - Change A: Batch/streaming ingestion endpoint (data-plane API enhancement)
   - Change B: Server-side corpus progress tracking (new domain concept)
   - Change C: Companion scaling (parallel processing, checkpointing, large-document handling)
   - Change D: Embedding throughput (may be infrastructure, not code)
3. **Do NOT couple to AdminApp WIP** — Preserve and isolate it. If a BFF is needed for historical ingestion, scope it as a separate minimal endpoint.
4. **Start with the smallest verifiable slice** — Likely a batch ingestion endpoint with tests, since it unblocks both Companion scaling and any future bulk tool.

## Risks

| Risk | Severity | Mitigation |
| --- | --- | --- |
| 500 GB corpus has unknown format distribution | High | Need actual file inventory before design |
| Extraction capacity at 500 GB is unvalidated | High | No benchmarks exist for sequential extraction throughput, LibreOffice sustained-batch reliability, operator-machine disk/RAM headroom, or total runtime. Benchmark on a representative corpus sample before design assumes extraction is a non-bottleneck. |
| CPU-only embedding is the real bottleneck | High | Profile embedding throughput on representative sample |
| AdminApp WIP entanglement | Medium | Strict isolation; this change does not touch AdminApp code |
| Staging/production environment uncertainty | Medium | Clarify target environment before any deployment work |
| `.git/index.lock` stale file | Low | Do not touch; not this change's concern |
