# Design: Evidence-Gated Historical Ingestion

## Decision summary

Build a Windows-local loader as a **headless .NET engine with a replaceable desktop shell**, backed by a local SQLite database and content-addressed staging files. The engine owns inventory, sample selection, checkpoints, extraction, submission, retry, audit, and benchmark evidence; the shell owns presentation and operator commands only.

The first implementation work unit is a read-only inventory pilot. It does not ingest documents, change the API, reuse Companion as a production dependency, or depend on AdminApp. Subsequent work units are gated by measured corpus and throughput evidence. Full-corpus ingestion and production deployment remain separate decisions.

## Evidence boundary

This design relies on the following verified repository behavior:

| Evidence | Architectural consequence |
| --- | --- |
| `src/Rag.Api/Program.cs` exposes `POST /api/v1/auth/token`, the existing TXT ingestion route, and operation polling. The TXT body is capped at 1,048,576 bytes. | Preserve the existing route and add a distinct, streaming historical contract rather than enlarging or repurposing the real-time route. |
| `src/Rag.Application/AcceptTxtIngestion.cs` already serializes by external reference, creates content-addressed versions, and returns duplicates idempotently for matching content. | Reuse the application concepts behind a new historical handler, but bind document identity to source provenance rather than normalized text alone. |
| `src/Rag.Infrastructure/OperationWorker.cs` processes one claimed operation at a time; PostgreSQL leases permit safe claims. | Do not claim server throughput or safe worker concurrency. Add admission/fairness boundaries and benchmark before changing worker parallelism. |
| `src/Rag.Infrastructure/JwtAuthentication.cs` issues 15-minute JWTs with client and credential identity but no scope claim. `Program.cs` validates credential status/version on every authenticated request. | Extend the credential grant and token claims additively; retain the 15-minute lifetime and immediate rotation/revocation invalidation. |
| `src/Rag.Companion/Host/CompanionRun.cs` acquires a BFF lease, walks files sequentially, and has no durable per-file checkpoint. | Do not use the Companion host as the historical orchestrator. Probe adapters independently and decide reuse/adapt/reference later. |
| `src/Rag.Companion/Ingestion/IngestionClient.cs` already performs direct token exchange, bounded transient retries, idempotent TXT submission, and polling. | Treat it as evidence and a benchmark reference, not as the required engine implementation. Its content-hash external reference is insufficient for source-level provenance. |
| `src/Rag.Companion/Rag.Companion.csproj` targets `net10.0` and `win-x64`; extraction supports `.doc`, `.docx`, `.md`, `.pdf`, and `.txt`. | Use .NET 10 for the proof to reduce integration uncertainty, but do not infer ARM64 or large-corpus suitability from the current Windows build. |
| Existing deployment documentation keeps PostgreSQL and llama.cpp internal. Cloudflare integration is not delivered by the current committed API. | Define the boundary contract now, but validate concrete Cloudflare and platform configuration only in environment-specific work units. |

No benchmark has yet established corpus count, sample size, throughput, safe concurrency, payload limits, CPU capacity, sustained LibreOffice reliability, or deployment feasibility. This design deliberately assigns those values to evidence gates rather than inventing them.

## Scope and protected boundaries

### Included

- Windows-local inventory, sampling, benchmark, headless execution, desktop controls, durable recovery, and privacy-preserving local audit.
- A separate historical data-plane API surface with scoped direct authentication, streamed content, idempotency, provenance, and operation telemetry.
- A searchable `legacy` collection with no automatic project taxonomy.
- Validation planning for local proof, OCI/Dokploy ARM64 staging, and Coolify.

### Protected and excluded

- The existing `POST /api/v1/collections/{id}/ingestions:txt` request/response contract and real-time workflow remain compatible.
- PostgreSQL, llama.cpp, content storage, and administrative services remain non-public.
- Existing AdminApp WIP under `src/Rag.AdminApp/`, `src/Rag.AdminApp.Host/`, its Docker target, Compose work, tests, and CI work is preserved and isolated.
- No inventory, extraction, ingestion, or filesystem traversal runs in AdminApp.
- No full-corpus run, production deployment, GPU/external embedding service, automatic classification, or performance promise is authorized here.

## System context

```text
Windows operator
    |
    v
Rag.HistoricalLoader.Desktop (replaceable WPF shell)
    | local-user-only named pipe: commands, snapshots, event stream
    v
Rag.HistoricalLoader.Engine (headless process)
    |-- Rag.HistoricalLoader.Core
    |-- SQLite run store (WAL, single writer)
    |-- content-addressed local staging directory
    |-- Windows Credential Manager
    |-- filesystem roots (read-only inventory; bounded snapshot reads for benchmark/run)
    |
    | HTTPS + Cloudflare service-token headers + short-lived API bearer token
    v
Cloudflare Access boundary
    v
Rag.Api historical endpoints
    |-- PostgreSQL metadata/operations (internal)
    |-- immutable content store (internal)
    `-- llama.cpp embedding runtime (internal)
```

The desktop shell never traverses source folders, opens SQLite, handles reusable secrets, calls the remote API, or imports Companion. The headless engine exposes a versioned local contract and can run inventory, benchmark, recovery, and fault-injection scenarios without a GUI. This boundary allows benchmark evidence to determine the extraction implementation without replacing the operator UI.

## Technology decisions

| Area | Decision | Reason / decision gate |
| --- | --- | --- |
| Engine/runtime | Select .NET 10 for the proof. | Matches the repository and current Windows extraction code; avoids introducing a second runtime before evidence. |
| Desktop shell | Select WPF provisionally for the first Windows proof, behind the local engine contract. | Windows is the required client platform, WPF is mature and keeps the proof in .NET. Electron is not assumed. Reconsider only if accessibility, packaging, or UI delivery evidence fails. |
| Engine process boundary | Select a separate headless executable with a current-user ACL Windows named pipe. | Keeps UI replaceable, supports restart/fault testing, and avoids opening a localhost TCP service. |
| Durable local queue/state | Select SQLite in WAL mode, foreign keys enabled, `synchronous=FULL`, and one serialized writer. | A single-machine workload needs indexed, transactional state for potentially many candidates. JSON files alone make atomic transitions, query, and recovery fragile. |
| Staged extracted content | Select files named by normalized-text SHA-256, atomically renamed into an app-owned directory; store only references in SQLite. | Avoids large SQLite BLOBs, permits checksum verification, and supports retry without re-extraction. |
| Inter-stage flow | Select bounded `Channel<T>` buffers fed from durable SQLite claims; SQLite remains the source of truth. | Provides in-process backpressure without introducing a broker. Channels may be lost on crash without losing work. |
| External queue/broker | Defer. | There is no evidence that one Windows engine or the current server queue is insufficient. Distributed processing is a later scaling decision. |
| Manifest export | Select a versioned `manifest.json` index/summary plus streaming `candidates.ndjson`; produce a human-readable Markdown summary. | A single giant JSON array may be unsafe for very large inventories. The index is valid JSON and the record stream is bounded-memory structured JSON. |
| Long-lived local secrets | Select Windows Credential Manager. | It is OS-appropriate and avoids config/checkpoint/log persistence. Access tokens remain memory-only and are reacquired after restart. |
| Companion | Defer reuse/adapt/reference until the benchmark gate. | Current adapters are useful evidence, but the host is sequential, BFF-coupled, and non-durable. |
| Concurrency, sample size, accepted text size, throughput, and capacity | Defer numeric values. | Inventory and benchmark reports must establish them. The proof begins at concurrency one and changes only during recorded benchmark sweeps. |
| Cloudflare configuration details | Defer exact policy/product configuration to the staging work unit. | The required logical boundary is defined below, but the repository does not currently deliver Cloudflare integration. |

## Local data model

SQLite schema migrations are versioned and backed up with the SQLite backup API before migration. Raw database-file copying while the engine is active is prohibited. Startup runs a quick integrity check; a failed check blocks processing and offers restore/export rather than silently rebuilding state.

| Entity | Essential fields and invariants |
| --- | --- |
| `loader_installation` | Stable random installation/namespace ID and schema version. No credential material. |
| `source_root` | Stable root ID, operator label, canonical absolute path, created time. Absolute paths remain local and are not included in logs or remote provenance. Reparse traversal is disabled by default. |
| `manifest` | Manifest ID, scan version, root set, start/end time, completion state, selection rules, summary totals, export hashes. An incomplete scan is never represented as complete. |
| `candidate` | Candidate ID, root ID, normalized relative path, extension/format, byte size, last-write time, eligibility code, discovery error code, and metadata fingerprint. Unique by manifest/root/path. |
| `sample_set` | Sample ID, manifest ID, algorithm version, seed, requested budget/statistical parameters, actual allocations, coverage limitations, and frozen candidate fingerprints. |
| `run` | Run ID, sample/batch ID, target environment/collection, desired state, observed state, engine version, configuration snapshot without secrets, start/end/checkpoint times. |
| `run_document` | Run ID, candidate ID, source-document key, current lifecycle state, attempt counters by network operation, extraction/upload hashes, remote upload/document/version/operation IDs, timestamps, and terminal classification. Unique by run/candidate. |
| `audit_event` | Monotonic event ID, UTC timestamp, run/candidate IDs when applicable, allowlisted action, state transition, outcome/error code, attempt number, and safe numeric measurements. |
| `benchmark_observation` | Environment and machine fingerprint, candidate/stratum, adapter/version, timings, byte/chunk counts, CPU/RAM/disk observations, queue wait, timeout/hang classification, and measurement method. |

### Candidate identity and drift

- `candidate_id` identifies one manifest record.
- `source_document_key` is derived with a versioned UUID-v5-style algorithm from the loader installation namespace, stable source-root ID, and Windows case-folded normalized relative path. Only the opaque key is sent remotely.
- Inventory reads metadata only. It does not hash or extract file content.
- Before extraction, the engine snapshots a file through a confined reader and verifies size/last-write metadata before and after copy. Drift produces a classified `source_changed` skip and requires re-inventory; a changing file is not ingested from an ambiguous snapshot.
- Reparse points, paths escaping a configured root, unsupported formats, inaccessible files, and filesystem errors receive explicit eligibility/error codes.
- A rename or move is a new source identity unless a later, explicit reconciliation feature links it. The proof does not silently infer identity from equal content.

## Inventory and representative sample selection

### Inventory flow

1. Persist a `manifest` in `scanning` state and the configured root aliases.
2. Enumerate each root with bounded memory, never following reparse points by default.
3. Persist every encountered candidate or enumeration error in small transactions through the single writer.
4. Classify supported formats, exclusions, access failures, and size/eligibility outcomes without reading content.
5. Compute format counts, total bytes, empirical size distribution, excluded counts/reasons, and incomplete-root warnings from stored rows.
6. Atomically mark the manifest complete only after every root reaches a terminal scan outcome.
7. Export `manifest.json`, `candidates.ndjson`, and `summary.md` to a new directory, fsync/close temporary files, atomically rename, and record hashes.

A partially scanned or partially exported manifest remains inspectable but cannot feed sample selection unless the operator explicitly accepts the listed coverage limitation; that acceptance is audited.

### Sample selection

Selection is deterministic and reproducible:

- Stratify eligible candidates by observed format and empirical size band. Size-band boundaries are stored in the manifest and are not hard-coded as capacity claims.
- Allocate the operator-approved sample budget proportionally, with a recorded minimum for every non-empty stratum and explicit boundary/outlier cases.
- Use a recorded PRNG algorithm and seed. Freeze each selected candidate's metadata fingerprint.
- Report population count, selected count, allocation, weighting, uncovered strata, and known limitations.
- If the supplied budget cannot cover every observed stratum, the sample gate fails rather than calling the result representative.
- “Representative” is limited to observed format and size dimensions. It does not imply coverage of hidden document complexity or a guaranteed full-corpus duration.

Confidence/margin targets, per-stratum minimums, sustained-run duration, and sample budget must be chosen and recorded before the benchmark. The design does not invent those product thresholds.

## Durable lifecycle and checkpoint protocol

### Document states

```text
pending
  -> snapshotting -> extracting -> staged
  -> reserving -> uploading -> committing -> remote_pending -> loaded

snapshotting/extracting
  -> skipped_document_error
reserving/uploading/committing/polling
  -> retry_wait -> same stage (up to three dispatched attempts)
  -> retry_exhausted_network
any pre-terminal state
  -> interrupted (on recovery) -> last durable safe stage
any state
  -> blocked_auth or blocked_operator_action when global intervention is required
```

Terminal outcomes are `loaded`, `skipped_document_error`, and `retry_exhausted_network`. `remote_pending`, `blocked_auth`, and `blocked_operator_action` are durable non-terminal states requiring reconciliation or intervention. “Loaded” is written only after the server reports a successful operation (or an idempotent existing successful result) and the local transaction records all returned identifiers.

Every transition and matching audit event commit in one SQLite transaction before the UI is notified. UI counters are projections from durable rows, never authoritative in-memory counters.

### Pause, resume, and restart

- `Pause` first commits `run.desired_state = pause_requested`.
- The scheduler stops claiming new documents. Active stages stop only at safe boundaries: a completed snapshot, atomically staged extraction, a persisted remote upload receipt, or a persisted remote operation ID.
- A remote operation already accepted by the API is not cancelled or called loaded. Its durable state remains `remote_pending`; later resume/reconciliation observes its server outcome.
- When no local stage lacks a durable receipt, the engine commits `run.observed_state = paused` and the UI reports a confirmed checkpoint.
- Forced termination may leave transient states. On startup, recovery examines durable receipts and staged-file hashes: local-only transient work returns to its preceding safe state; remote-ambiguous work is reconciled by idempotency/status before any upload or commit is repeated.
- Resume never resets attempt counts. Previously loaded, skipped, or retry-exhausted records are not made pending automatically.
- An operator may create an explicitly audited retry generation or a new run for terminal errors; restart alone cannot bypass the three-attempt rule.

## Idempotency, versions, and deduplication

The historical path uses three distinct concepts:

1. **Source identity:** `(collection_id, source_document_key)` identifies one provenance-bearing document.
2. **Version identity:** `(document_id, normalized_text_sha256)` identifies content already accepted for that source.
3. **Request idempotency:** `(service_client_id, idempotency_key)` binds one API mutation to a canonical request fingerprint and result.

Rules:

- Retrying the same candidate/content returns the same upload/version/operation identifiers.
- Reusing an idempotency key with different metadata or hash returns `409 Conflict` and never mutates the existing record.
- Changed normalized content for the same source creates a new document version.
- Equal normalized content from two different source-document keys remains two documents so provenance is not lost. Physical blob reuse may be content-addressed internally, but semantic records are not collapsed.
- The server verifies declared length and SHA-256 while streaming before commit.
- The current Companion strategy of using normalized content hash as the external reference is not adopted for historical provenance.

## Concurrency and backpressure

There are four explicit capacity boundaries:

| Boundary | Mechanism | Initial state |
| --- | --- | --- |
| Filesystem snapshot/extraction | Durable SQLite claims plus a bounded channel; per-adapter concurrency limits. | One. LibreOffice remains one until sustained evidence permits otherwise. |
| Local staged data | Configured maximum count and bytes; scheduler stops extraction when either watermark is reached. | Numeric values set from inventory/operator disk budget, not this design. |
| HTTP mutations and polls | Separate bounded concurrency controls and persisted attempt counters. | One mutation; poll cadence is independent and jittered. |
| Server admission/embedding | Per-client/global pending quotas, `429 Retry-After`, workload classification, and real-time-preferred fair claiming. | Preserve current real-time behavior and worker concurrency until benchmark evidence. |

The API tags historical operations separately from real-time operations. Scheduling must prevent a historical backlog from starving real-time work. Existing real-time contract tests remain unchanged; existing clients receive compatibility grants during the scope migration. Worker parallelism is increased only after llama.cpp and database benchmarks show safe headroom.

An external message broker, distributed extractor, or multi-engine coordination is not selected. Evidence that one engine cannot meet an approved window triggers a new architecture decision rather than an unreviewed concurrency increase.

## Retry and failure policy

A dispatched HTTP operation has at most three total attempts per run/generation, persisted before dispatch. Successful polling that reports `pending` is not a failed retry; transport failure of that poll call is.

| Class | Examples | Policy |
| --- | --- | --- |
| Transient network/boundary | Timeout, connection failure, `425`, `429`, `502`, `503`, `504`, other `5xx` explicitly classified transient | Full-jitter exponential backoff, honor bounded `Retry-After`, maximum three dispatched attempts. Then persist `retry_exhausted_network`. |
| Authentication/authorization | Cloudflare denial, API `401`, expired/revoked/insufficient scope `403` | No document retry loop. Pause new submissions, clear memory token when appropriate, persist safe security event, require credential/boundary correction. |
| Document | Unsupported/corrupt format, size policy, extraction failure, source drift | Persist classified skip and continue later documents. Never retry as a network failure. |
| Contract/data conflict | Hash mismatch, idempotency fingerprint conflict, malformed API response | Fail closed as operator action; do not guess or overwrite. |
| Local capacity | Disk watermark, SQLite failure, staging integrity failure | Stop claiming, checkpoint what is safe, and block the run. Do not skip data silently. |

Cancellation requested before dispatch does not consume an attempt. Once dispatch begins, an unknown outcome consumes an attempt and must be reconciled through the idempotent resource before another mutation.

## Audit, logs, and privacy

- Audit is an append-only logical stream in SQLite for operator actions, lifecycle transitions, retries, skips, checkpoint/recovery events, and credential add/change/remove outcomes.
- Allowed fields are explicit. They include event/run/candidate IDs, stage, classification, outcome, timestamp, attempt, duration, safe counts, API request correlation ID, and remote resource IDs.
- Audit and ordinary logs exclude document text, extracted snippets, hashes of credentials/tokens, reusable secrets, bearer tokens, Cloudflare secret values, arbitrary request/response bodies, and absolute source paths.
- Local candidate details may resolve a path from the private manifest when the operator opens a document detail view; that view is not an audit field and is not uploaded.
- Exception mapping uses stable error codes and allowlisted bounded messages. Raw exception text is diagnostic-only after redaction and is disabled in exported reports by default.
- Full manifests and SQLite files are sensitive local artifacts. The installer creates an app-data directory restricted to the current user. Exports are redacted by default and clearly labelled when they contain relative paths.
- Reusable application and Cloudflare credentials live in Windows Credential Manager. Short-lived API access tokens are memory-only, are never checkpointed, and are reacquired after restart.
- Active-run audit is never automatically deleted. Retention/export deletion policy is a later operator decision and must preserve all evidence needed for the multi-night run.

Automated privacy tests scan serialized manifests, reports, audit, logs, and failure views using sentinel content, paths, client secrets, Cloudflare secrets, and bearer tokens.

## Direct loader-to-API contract

### Layered authentication

Two independent credentials cross the Windows-to-API boundary:

1. **Cloudflare service token** proves that the loader may reach the API hostname. The engine adds the required Access headers at the outer HTTPS boundary. The origin is not directly reachable.
2. **RAG service-client credential** is exchanged with the API for a 15-minute JWT. The JWT proves application identity, credential version/status, collection grant, and least-privilege scopes.

Cloudflare acceptance never replaces API JWT validation. API acceptance never bypasses Cloudflare outside loopback-only local development. The two credentials rotate and revoke independently and are stored as separate Windows Credential Manager entries.

### Token exchange extension

Keep `POST /api/v1/auth/token` and extend its JSON request additively:

```json
{
  "key_id": "opaque-key-id",
  "secret": "one-time-provisioned-secret",
  "scope": "historical:uploads.write historical:operations.read"
}
```

Response:

```json
{
  "access_token": "<redacted>",
  "token_type": "Bearer",
  "expires_in": 900,
  "scope": "historical:operations.read historical:uploads.write"
}
```

- Omitted `scope` preserves existing client exchange behavior.
- Existing service clients receive explicit compatibility grants before endpoint policies are enforced, preserving real-time behavior.
- A historical loader client receives only its approved historical scopes and a grant to the `legacy` collection. It cannot create arbitrary collections, search unrelated collections, use administrative routes, or use the real-time ingestion route.
- Unknown/ungranted scope is rejected as a safe `invalid_scope` response; absent/expired/revoked credentials remain `401`; valid but insufficient tokens receive `403`.
- JWTs include normalized scope claims, collection-grant identity/version, existing client/credential IDs, and credential version. Every request retains current credential-state validation.

### Streaming historical API

The route family is additive and separate from `ingestions:txt`:

| Operation | Contract |
| --- | --- |
| `POST /api/v1/historical/collections/{collectionId}/uploads` | Reserve or return an idempotent upload from source key, candidate/manifest/run IDs, safe provenance, normalized-text SHA-256, declared bytes, and request idempotency key. Returns upload ID, current state, accepted limits, and correlation ID. |
| `PUT /api/v1/historical/uploads/{uploadId}/content` | Stream `text/plain; charset=utf-8`; verify declared length and digest; atomically publish to internal immutable storage. Repeated matching upload is idempotent. No PostgreSQL/llama endpoint is exposed. |
| `POST /api/v1/historical/uploads/{uploadId}:commit` | Idempotently create/find the source document and version, persist provenance, enqueue a historical operation, and return document/version/operation IDs. |
| `GET /api/v1/historical/uploads/{uploadId}` | Reconcile an ambiguous reserve/upload/commit outcome without repeating content mutation. |
| `GET /api/v1/historical/collections/{collectionId}/operations/{operationId}` | Return safe state, failure stage/code, queue wait, chunk count, and stage timings needed for evidence. It never returns content or internal service addresses. |

The proof's maximum normalized-text size is an explicit server configuration recorded in benchmark evidence. It is not inherited from the 1 MiB real-time cap and is not left unlimited. Streaming code enforces request rate, declared length, observed length, content hash, abandoned-upload expiry, per-client pending quota, and total storage watermark.

### Credential lifecycle

- Bootstrap for the first proof uses the existing trusted operator path, not AdminApp. The operator receives the RAG secret once and writes it directly to Windows Credential Manager; config contains only credential entry names and public identifiers.
- For overlap rotation, provision a second active credential, update the loader entry, verify token exchange, then revoke the old credential. Immediate rotation/revocation invalidates old JWTs through credential version/status checks.
- Cloudflare service-token rotation follows the same overlap principle but remains a Cloudflare control-plane operation.
- Deleting a local credential entry stops future exchange but does not revoke the server credential; revocation is a separate audited control-plane action.
- Auth failures pause submissions globally. They do not transform all documents into document errors or consume three retries per candidate.

## Cloudflare and internal-service boundary

- Staging/target API ingress is an HTTPS hostname protected by a Cloudflare Access service-token policy. The origin is reachable only through the approved tunnel/private attachment and rejects direct internet ingress.
- Cloudflare strips or controls identity headers at the edge. The loader supplies only the documented service-token headers; those values never enter application logs.
- Only the RAG API historical route family and required health behavior cross this boundary. PostgreSQL, llama.cpp, content volumes, metrics backends, and administrative endpoints have no public listener or loader route.
- Local development is loopback-only and may omit Cloudflare while retaining application JWT validation. Evidence must clearly label this exception.
- Exact Cloudflare account policy, token lifetime, tunnel topology, and secret injection are staging decisions. A configuration cannot pass merely because the application tests pass; origin exposure and denied-direct-access checks are required.

## Legacy collection and provenance

A dedicated collection named `legacy` is provisioned outside the loader's local workflow and bound to the historical loader client. The loader is granted write/status access only to that collection.

Each historical document version stores an allowlisted provenance record:

- `source_kind = historical_windows_manifest`
- manifest ID and candidate ID
- opaque source-document key
- source-root alias (not absolute root)
- original display filename and format
- source byte size and last-write timestamp observed at snapshot
- source snapshot SHA-256 and normalized-text SHA-256
- run ID, loader version, extraction adapter/version
- ingestion timestamp and remote operation ID

The private local manifest retains the root/path mapping. Retrieval metadata can associate a result with the exact manifest candidate without asserting a project, taxonomy, owner, or inferred classification. A separate future workflow may classify legacy data; this loader does not.

## Observability and capacity evidence

### Local measurements

The headless engine records per-candidate discovery, snapshot, extraction, staging, upload, commit, poll, and end-to-end durations; source/normalized bytes; error/timeout/hang code; retry count; staged backlog; and process CPU, working set, and disk-I/O counters. Method, sampling interval, machine/OS/runtime version, adapter version, and configuration accompany every report.

### Server measurements

Safe operation telemetry records queue wait, chunk count, chunking duration, embedding request count/duration, indexing duration, terminal state, active/pending historical counts, real-time queue impact, and rate/quota rejections. A metrics backend is not selected yet; structured operation evidence is part of the API contract, while an OpenTelemetry/export backend may be chosen in the environment work unit. Internal metrics are not exposed to the loader or internet.

### Benchmark matrix and report

Run cold and warm observations across every sampled format/size stratum and recorded concurrency sweep. Include a sustained extraction run long enough to exercise the predeclared reliability criterion. Report count, median, p95 where sample size supports it, observed range, documents/hour, source GB/hour, normalized GB/hour, embedding chunks/second, error rate, timeouts/hangs, peak resources, queue growth, and confidence/coverage limitations.

Before execution, stakeholders record:

- acceptable completion window for any future full-corpus run;
- minimum resource headroom and maximum operator impact;
- acceptable extraction timeout/hang/error rates;
- sample confidence/coverage rules;
- maximum safe queue growth and real-time latency impact.

The report may estimate a full-corpus range by weighting measured strata, but must show assumptions and uncertainty. It cannot declare feasibility when strata are uncovered, sustained reliability fails, queue growth is unbounded, or the estimate misses the predeclared window.

## Companion decision gate

The benchmark harness may invoke current Companion adapters through a thin probe without modifying `src/Rag.Companion/`; the probe is not a production dependency. After inventory and benchmark evidence, record exactly one disposition:

| Disposition | Evidence gate |
| --- | --- |
| Reuse as headless core | Adapters meet predeclared reliability/resource criteria; they can be isolated from BFF host concerns behind the engine extractor interface; required changes are bounded and testable. |
| Adapt | Adapter behavior meets criteria, but hosting, configuration, sequential orchestration, or coupling requires extracting/refactoring the adapter layer. |
| Reference only | Reliability, process control, packaging, architecture coupling, or required rewrite makes reuse more costly/risky than a clean extractor implementation. |

The decision cites observation IDs/report sections and limitations. No production engine project references Companion until this gate passes. The WPF shell is unaffected by the outcome because it speaks only to the headless engine contract.

## Validation gates

### Gate L — Windows-local proof

Required evidence before staging work:

- complete inventory accounting and reproducible sample coverage;
- benchmark report with extraction/embedding/resource/sustained-reliability observations and Companion disposition;
- at least 100 selected proof documents processed through the new historical API into `legacy`;
- successful search result with attributable provenance and no automatic taxonomy;
- pause reaches a durable checkpoint; kill/restart resumes without reloading confirmed documents;
- transient fault injection proves no fourth attempt; document faults do not block later documents;
- idempotent unknown-outcome reconciliation and hash-conflict tests;
- token expiry, insufficient scope, application revocation, and Cloudflare-local-exception labeling;
- privacy sentinel scan and proof that only loopback API access is used locally;
- real-time ingestion compatibility contract tests remain green.

Failure blocks OCI/Dokploy validation.

### Gate A — OCI/Dokploy ARM64 staging

Use only a bounded non-production sample. Required evidence:

- verified native `linux/arm64` API/operator/runtime images and exact immutable image identity;
- migration, rollback/restore rehearsal, readiness, and internal-only PostgreSQL/llama access;
- Cloudflare service-token allow/deny checks and direct-origin denial;
- historical scope/revocation/idempotency/provenance contract tests;
- llama.cpp CPU embedding observations on the actual ARM64 host, including resource headroom and real-time queue impact;
- comparison with local evidence and documented regressions/limitations;
- no dependency on a Windows Companion binary on the server.

Failure blocks Coolify validation.

### Gate C — Coolify target validation

This is target-environment validation, not production authorization. Required evidence:

- signed/verified immutable multi-platform image references and target architecture;
- secret/config inventory with no secret in Compose, manifests, logs, or reports;
- Cloudflare-only ingress and no direct internal-service exposure;
- additive migration and backup/restore evidence;
- bounded proof-run checkpoint/auth/provenance/search evidence matching prior gates;
- observability and capacity comparison with staging;
- explicit rollback procedure and confirmation that no full-corpus run occurred.

### Future full-corpus decision

A separate decision record may authorize or reject scaling only after Gates L, A, and C. It must cite the inventory, weighted capacity range, reliability evidence, operator window, infrastructure headroom, Companion disposition, rollback/backup readiness, and unresolved risks. This design does not authorize that decision in advance.

## Projected file changes

These are implementation targets, not files created by this design phase. Exact names may be refined by tasks without crossing the boundaries below.

| Area | Projected paths | Purpose |
| --- | --- | --- |
| Local contracts/domain | `src/Rag.HistoricalLoader.Core/` | Versioned manifest, sampling, lifecycle, extractor/API interfaces, safe events, benchmark model. |
| Headless host | `src/Rag.HistoricalLoader.Engine/` | SQLite/staging ownership, scheduling, Windows credentials, Cloudflare/API clients, named-pipe host. |
| Desktop shell | `src/Rag.HistoricalLoader.Desktop/` | WPF inventory/run/audit/error views and commands only. |
| Local tests | `tests/Rag.HistoricalLoader.UnitTests/`, `tests/Rag.HistoricalLoader.IntegrationTests/` | Strict-TDD state, crash, filesystem, privacy, IPC, and API-fake verification. |
| Historical API | New focused files under `src/Rag.Api/` and `src/Rag.Application/` | Additive token scope and historical upload/status endpoints/handlers. Keep existing TXT route shape unchanged. |
| Server domain/storage | Focused additions under `src/Rag.Domain/` and `src/Rag.Infrastructure/` plus additive migration | Grants, idempotency, uploads, provenance, workload class, admission and safe telemetry. |
| API/integration tests | Existing `tests/Rag.UnitTests/` and `tests/Rag.IntegrationTests/` | Scope migration, streaming limits, dedup/idempotency, provenance, fairness, privacy, real-time compatibility. |
| Environment evidence/docs | Dedicated later deployment-validation documentation and fixtures | ARM64/Dokploy/Coolify gates; no deployment in planning or local proof work units. |

Protected paths in the historical work units: `src/Rag.AdminApp/`, `src/Rag.AdminApp.Host/`, AdminApp Docker/Compose/CI work, and `src/Rag.Companion/` until the Companion gate explicitly authorizes a bounded follow-up.

## Review-budget-aware work units

Delivery is a sequence of independently testable work units. Tests and user-facing documentation stay with each behavior. Tasks must forecast authored changed lines before apply; approach to 400 lines triggers a chained PR boundary rather than code compression. If the smallest honest unit exceeds the budget, report the exact forecast and request a size exception instead of repeatedly slicing away cohesion.

| Unit | Deliverable | Independent verification / gate |
| --- | --- | --- |
| 1 — Smallest viable first unit | Read-only headless inventory for configured Windows roots, canonical local manifest persistence, complete/incomplete semantics, structured export, and human summary. No API/UI/Companion/AdminApp changes. | Fixture-tree tests account for eligible, unsupported, inaccessible, reparse, and partial-enumeration outcomes; no content is opened; export hashes and summary totals reconcile. |
| 2 | Deterministic stratified sample selection and frozen fingerprints. | Golden selection from a fixed manifest/seed; uncovered strata fail representative status; changed candidates invalidate selection. |
| 3 | Extraction benchmark probe and local resource observations, including sustained LibreOffice evidence. | Per-format/size observations and fault/timeout records; no production dependency decision yet. |
| 4 | Server-side safe timing evidence needed for embedding benchmark, without changing real-time wire behavior. | Bounded local sample produces chunk/queue/embedding timings; existing real-time tests remain green. |
| 5 | Companion disposition record and extractor-interface decision. | Report cites benchmark observations and selects reuse/adapt/reference; unresolved evidence blocks progression. |
| 6 | SQLite run/lifecycle/checkpoint engine with fake extractor and fake API. | Power-loss/restart, pause drain, staged-hash recovery, terminal-state immutability, and persistent attempt tests. |
| 7 | Additive service-client grants, scope-bearing token exchange, and compatibility migration. | Existing clients retain behavior; loader token is denied outside `legacy` historical scopes; expiry/rotation/revocation tests pass. |
| 8 | Historical upload reservation/stream/commit/status API with provenance and workload class. | Streaming limits/hash, idempotency conflict, same-source versioning, cross-source provenance, admission, fairness, and operation telemetry tests. |
| 9 | Engine API client, Windows Credential Manager integration, Cloudflare header boundary, and global auth-block behavior. | Secret sentinel tests, token restart/expiry, ambiguous mutation reconciliation, edge/API denial classification. |
| 10 | Bounded pipeline, persisted retries, staged watermarks, pause/resume, and selected extractor implementation. | Three-attempt ceiling across restart; document skip continuity; bounded queues; no fourth attempt; no unconfirmed loaded state. |
| 11 | Replaceable WPF operator shell. | UI automation/view-model tests cover inventory, start, pausing/paused distinction, resume, states, audit, and errors without terminal use. |
| 12 | Windows local vertical proof and evidence pack. | Gate L, including at least 100 documents, search/provenance, restart, faults, privacy, benchmark, and real-time compatibility. |
| 13 | OCI/Dokploy ARM64 bounded validation. | Gate A only; no production/full-corpus run. Infrastructure changes, if any, are isolated from AdminApp WIP and separately reviewed. |
| 14 | Coolify target bounded validation and full-corpus decision packet. | Gate C evidence and rollback; scaling remains unapproved until a separate decision. |
| Future separate change | AdminApp provisioning/revocation integration. | May begin only after direct-auth API proof; never adds traversal/ingestion execution to AdminApp and cannot retroactively block Gate L. |

Units 1–5 form the evidence tranche; Units 6–12 form the local proof tranche; Units 13–14 form environment validation. Auto-chain should apply only the next accepted unit, preserving a green and comprehensible state after each unit.

## Test strategy

Strict TDD applies to implementation units:

- **Unit tests:** path normalization/classification, sampling allocation, lifecycle transition table, retry accounting, audit allowlist/redaction, idempotency fingerprints, scope evaluation, and benchmark aggregation.
- **SQLite integration:** WAL recovery, transaction/audit atomicity, migration backup, corrupted/stale staging references, concurrent read/single-write behavior, and restart reconciliation.
- **Filesystem tests on Windows:** inaccessible entries, reparse/junction escape, sharing violation, file drift, long/Unicode/case-equivalent paths, and atomic export/staging.
- **API integration:** streaming size/hash limits, repeated reserve/upload/commit, changed fingerprint conflict, version semantics, collection grants, token expiry/revocation/scope, pending quotas, real-time fairness, and safe telemetry.
- **Fault injection:** kill after each durable boundary; timeout/connection/429/5xx sequences; unknown response after server commit; disk watermark; Credential Manager unavailable; Cloudflare versus application denial.
- **End to end:** WPF shell to headless engine to protected API, local proof batch of at least 100, pause/kill/restart/resume, searchable legacy result and provenance.
- **Compatibility:** existing TXT ingestion, retrieval, collection ownership, operation leases, and current clients remain behaviorally compatible.
- **Privacy:** serialized-output scans prove sentinels for content, absolute paths, RAG secrets, Cloudflare secrets, and bearer tokens are absent from audit/log/report surfaces.
- **Environment:** the same contract/fault suite produces evidence for local, ARM64 staging, and Coolify with immutable environment identity.

No benchmark test hard-codes a desired throughput. It verifies measurement integrity, coverage, and decision rules.

## Rollout and rollback

1. Ship inventory/sampling as local-only behavior with no server dependency.
2. Produce benchmark evidence and decide Companion disposition.
3. Add server grants and historical endpoints behind `HistoricalIngestion:Enabled = false` by default; run compatibility migrations/tests before enabling locally.
4. Enable only in loopback development for the bounded proof and dedicated `legacy` collection.
5. After Gate L, enable for the dedicated staging client/collection through Cloudflare; then evaluate Gate A.
6. After Gate A, perform bounded Coolify target validation; do not start the full corpus.

Rollback stops the local engine, disables the historical feature flag, revokes the loader and Cloudflare credentials, and preserves SQLite/evidence for diagnosis. Additive server tables may remain inert. Deleting `legacy`, staged content, upload records, or migrations is a separate destructive action requiring backup and explicit approval. Existing real-time routes remain available throughout.

## AdminApp disposition

AdminApp WIP is preserved exactly and isolated from this change's first proof. It is **not** a dependency, host, UI shell, secret store, filesystem agent, or fallback orchestrator. The trusted operator path provisions the proof credential. Only after the direct-auth contract and local proof pass may a separate future change integrate AdminApp as a control plane for provisioning and revocation. That future work owns its own source, Compose, CI, security, and review gates and cannot block inventory, benchmark, or the first local vertical proof.

## Open decisions with required evidence

| Decision | Evidence required | Blocking point |
| --- | --- | --- |
| Sample parameters and sustained-run duration | Completed inventory plus predeclared confidence/coverage/reliability goals | Before benchmark execution |
| Companion reuse/adapt/reference | Extraction and sustained reliability observations plus coupling/change assessment | Before production extractor implementation |
| Safe local stage/channel capacities | Disk budget and observed extraction/upload rates | Before proof pipeline concurrency above one |
| Safe server admission and worker concurrency | Queue, llama.cpp CPU/RAM, DB, and real-time impact observations | Before increasing server concurrency |
| Historical normalized-text size limit | Inventory/extraction distribution and server storage/processing evidence | Before API proof configuration |
| Full-corpus feasibility | Weighted capacity range, operator window, all environment gates | Separate future decision |
| Cloudflare concrete topology/lifetimes | Staging configuration and direct-origin denial evidence | Gate A |
| UI technology beyond proof | WPF accessibility, packaging, and operator feedback | After Gate L; does not affect engine evidence |
| Distributed extraction, broker, GPU, or external embeddings | Demonstrated failure of approved window/headroom with current bounded architecture | Separate scoped proposal |
