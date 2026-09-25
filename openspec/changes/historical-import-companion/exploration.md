# Exploration: Historical Import Companion

## Purpose

Explore the design space for a local companion process that a trusted administrator runs on a machine with Dropbox-synchronized folders to perform a one-time snapshot import of historical RAG content.

## Current State

### Existing Ingestion API (implemented)

The RAG API already exposes a complete data-plane ingestion flow:

| Endpoint | Auth | Description |
|---|---|---|
| `POST /api/v1/auth/token` | None (rate-limited) | KeyId + Secret → JWT Bearer (15 min, RSA-SHA256) |
| `POST /api/v1/collections` | JWT | Create collection owned by authenticated client |
| `POST /api/v1/collections/{id}/ingestions:txt` | JWT | Submit TXT content (JSON body: `file_name`, `content`, `external_reference`; max 1 MB UTF-8) |
| `GET /api/v1/collections/{id}/operations/{opId}` | JWT | Poll async operation status |
| `POST /api/v1/retrieval:search` | JWT | Semantic search |

**Authentication**: Token exchange (D-101 Accepted). Client sends `KeyId` + `Secret` → receives short-lived JWT containing `credential_id`, `client_id`, `credential_version`. All data-plane endpoints require this Bearer token.

**Document model**: Only `.txt` supported (D-014 vertical slice). Max body 1 MB. `external_reference` is an optional opaque string enabling dedup: same reference + same content hash → idempotent return; same reference + different hash → new `DocumentVersion`. Content stored via `IImmutableContentStore` (filesystem implementation exists).

**Collection model**: Collections are owned by a service client. Collection grants control cross-client access. Each collection has an embedding profile.

### Companion Protocol (designed, not implemented)

The `admin-application` design defines the BFF side of a pull-based companion protocol:

- **Lease**: `POST /companion/v1/import-jobs/lease` → `{jobId, collectionId, leaseId, leaseExpiresAt, nextSequence, snapshot:true}` or `204` when no work.
- **Events**: `POST /companion/v1/import-jobs/{jobId}/events` → `{protocolVersion:1, leaseId, eventId, sequence, state, processed, failed, errorCode?}`.
- **Identity**: HMAC-SHA256 headers (`X-Companion-Id`, `X-Companion-Key-Id`, `X-Companion-Timestamp`, `X-Companion-Nonce`, `X-Companion-Body-SHA256`, `X-Companion-Signature`). Signature = Base64(`METHOD\nPATH\nBODY_SHA256\nCOMPANION_ID\nKEY_ID\nTIMESTAMP\nNONCE`). Timestamp = Unix seconds UTC, 60 s tolerance. Body hash = lowercase hex SHA-256 of exact UTF-8 body. Nonces single-use. Leases 60 s.
- **State machine**: `requested → running → succeeded|failed`; `running → requested` on lease expiry. Sequence monotonic; counts non-decreasing. Duplicate `eventId` idempotent. Terminal states immutable.
- **Content boundary**: No file paths, content, commands, or provider metadata in events. Unknown fields → `400`.

### Existing Runtime Patterns

- .NET 10 / ASP.NET Core, modular monolith.
- `Rag.Operator` is a CLI tool sharing `Rag.Application` + `Rag.Infrastructure` for migrations and credential management.
- Dockerfile multi-stage: `api` and `operator` targets. Compose for dev and Coolify.
- Integration tests use Testcontainers (`PostgreSqlFixture`). Unit tests mock application interfaces.
- xUnit + `WebApplicationFactory` for API integration tests.

## Affected Areas

- `src/Rag.Companion/` (new) — Companion CLI project.
- `Rag.sln` — Add new project reference.
- `Dockerfile` — Optional companion build target (or distribute as `dotnet tool` / self-contained binary).
- `openspec/changes/admin-application/` — No changes; the companion protocol contract is already defined there.
- `src/Rag.Api/` — No changes; companion consumes existing ingestion endpoints.
- `src/Rag.AdminApp.Host/` — No changes; BFF companion endpoints are designed but not yet implemented (separate change).

## Approaches

### 1. Standalone .NET CLI tool (`Rag.Companion`)

A new console project in the solution. Administrator runs it locally with configuration (directories, companion credentials, API base URL, target collection). It discovers `.txt` files, authenticates via token exchange, submits each through the existing ingestion API, and reports progress to the BFF companion protocol.

- **Pros**:
  - Same stack as the rest of the system; reuses `Rag.Application` types if needed.
  - HMAC signing implementation in C# is straightforward and testable.
  - Can be distributed as a self-contained single-file binary (no .NET runtime required on admin machine) or as a `dotnet tool`.
  - Follows the `Rag.Operator` CLI pattern already established.
  - Unit-testable signing, discovery, and reporting logic.
- **Cons**:
  - Requires building/publishing a separate binary.
  - Admin must obtain and securely store companion HMAC credentials and a service-client credential for the ingestion API.
- **Effort**: Medium.

### 2. Cross-platform script (Python / Bash)

A standalone script that performs directory scanning, HTTP calls, and HMAC signing.

- **Pros**:
  - No build step; runs anywhere with Python/bash.
- **Cons**:
  - Introduces a second stack; HMAC canonicalization must be reimplemented and kept in sync with the BFF verifier.
  - No type safety for the protocol contract.
  - Harder to test signing edge cases (timestamp tolerance, nonce reuse, body hash encoding).
  - Inconsistent with the project's .NET-first convention.
- **Effort**: Medium-High (due to signing correctness risk).

### 3. BackgroundService daemon

A long-running hosted service that polls for lease availability.

- **Pros**:
  - Could support future scheduled/sync use cases.
- **Cons**:
  - Overkill for one-time snapshot import.
  - Adds lifecycle complexity (start/stop, health checks, service installation).
  - Explicitly out of scope (no sync, no schedules).
- **Effort**: High.

## Recommendation

**Approach 1: Standalone .NET CLI tool (`Rag.Companion`)**.

Rationale:
- The companion is a one-time snapshot tool, not a daemon. A CLI matches the usage pattern (run, import, done).
- Same .NET 10 stack keeps signing, HTTP, and JSON handling consistent with the rest of the codebase.
- The `Rag.Operator` CLI already demonstrates the pattern of a separate console project sharing `Rag.Application`/`Rag.Infrastructure`.
- Self-contained publish (`dotnet publish --self-contained`) produces a single binary the admin can run without installing .NET.
- The companion protocol's HMAC signing is well-specified in the admin-application design; implementing it in C# with unit tests is the safest path to correctness.

### Companion Lifecycle

```text
1. Admin configures: API base URL, companion credentials (Id, KeyId, HMAC secret),
   service-client credentials (KeyId, Secret) for ingestion API, target collection ID,
   root directories to scan.
2. Admin runs: rag-companion run --config config.json (or env vars).
3. Companion polls BFF lease endpoint until a job is available.
4. On lease: scan configured directories for .txt files.
5. For each file:
   a. Compute external_reference (e.g., relative path or content hash).
   b. POST /api/v1/auth/token → JWT.
   c. POST /api/v1/collections/{id}/ingestions:txt → operation_id.
   d. Optionally poll operation status.
   e. Report progress event to BFF (sequence++, processed++ or failed++).
6. On completion: report terminal state (succeeded|failed) to BFF.
7. Exit.
```

### Key Design Decisions (companion-internal)

| Decision | Recommendation | Rationale |
|---|---|---|
| File discovery | Recursive `.txt` scan of configured root directories | Matches current API support; explicit scope avoids surprise files |
| External reference | Relative path from root directory (normalized, forward slashes) | Stable across re-runs; enables dedup on re-import |
| Concurrency | Sequential file submission (one at a time) | Snapshot import is bounded; simplifies progress tracking and error reporting |
| Failure handling | Count as `failed`, continue with next file; report `errorCode` | One bad file should not abort the entire import |
| Authentication caching | Cache JWT until expiry; refresh proactively | Avoid token exchange per file |
| Configuration | JSON config file + environment variable overrides | Flexible for admin; secrets via env vars |
| Logging | Structured console output (JSON or plain); no secrets | Admin needs visibility; no credential leakage |

### Questions Requiring Product Input

1. **File format scope**: Should the companion discover only `.txt` (matching current API) or also prepare for future formats (`.md`, `.pdf`)? Recommendation: `.txt` only for now; the companion can be extended when the API supports more formats.

2. **External reference strategy**: Should it be the relative file path, a content hash, or a Dropbox file ID? Recommendation: relative path (stable, human-readable, enables dedup).

3. **Multi-collection support**: Should a single companion run target one collection or multiple? Recommendation: one collection per run (matches the lease protocol's single `collectionId`).

4. **Retry on transient API failures**: Should the companion retry failed ingestion calls (e.g., 5xx, timeouts)? Recommendation: yes, with bounded exponential backoff (3 attempts, 1s/2s/4s).

5. **Credential provisioning**: How does the admin obtain companion HMAC credentials and service-client credentials? Recommendation: admin uses the admin UI (once available) or `Rag.Operator` to create a service client and credential; companion HMAC credentials are provisioned out-of-band by the system administrator.

## Risks

- **BFF companion endpoints not yet implemented**: The companion cannot be E2E tested against the BFF until `Rag.AdminApp.Host` implements the lease/event routes. Mitigation: companion can be developed and tested against a fake/mock BFF; integration tests use `WebApplicationFactory`.
- **HMAC signing correctness**: The canonicalization must match the BFF verifier exactly (header order, encoding, body hash format). Mitigation: shared test vectors; property-based tests for signing round-trips.
- **Dropbox sync timing**: Files may be in intermediate sync state (`.tmp`, partial downloads). Mitigation: skip files with Dropbox temporary extensions; document that the admin should ensure sync is complete before running.
- **Large imports**: Hundreds of files could take significant time; lease may expire. Mitigation: running events renew the lease (per protocol); companion should send periodic heartbeats for long-running imports.
- **Secret management**: Admin must store HMAC secret and service-client secret securely. Mitigation: companion reads secrets from environment variables or a config file with restricted permissions; never logs them.

## Ready for Proposal

**Yes.** The exploration has sufficient context to proceed to proposal. The orchestrator should inform the user that:

1. The companion will be a standalone .NET CLI tool (`Rag.Companion`) following the `Rag.Operator` pattern.
2. It consumes the existing ingestion API (token exchange + TXT ingestion) — no API changes needed.
3. It reports progress through the already-designed companion protocol (lease/events with HMAC signing).
4. Five product-input questions are identified (file format scope, external reference strategy, multi-collection support, retry policy, credential provisioning) — recommendations are provided for each.
5. The BFF companion endpoints must be implemented (in the `admin-application` change) before E2E testing is possible, but the companion can be developed independently with mock BFF tests.
