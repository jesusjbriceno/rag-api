# Design: Architecture Boundaries

## Technical Approach

Adopt the accepted D-003 through D-006 baseline: a .NET/ASP.NET Core modular monolith backed by PostgreSQL/pgvector, with a local background worker inside the modular deployable for long-running ingestion. API delegates use cases to Application, Domain owns invariants, and technical boundaries remain outward-facing adapters. This scope correction changes documentation only.

## Architecture Decisions

| Option | Tradeoff | Decision and rationale |
|---|---|---|
| .NET/ASP.NET Core vs alternatives | Smaller local ML ecosystem vs typed service platform | Accept D-004 because this service consumes AI capabilities and benefits from a stable HTTP platform. |
| PostgreSQL/pgvector vs separate stores | Shared operational boundary vs specialized systems | Accept D-003 for relational and vector persistence without adding infrastructure. |
| Modular monolith vs microservices | One deployment vs independent scaling | Accept D-005; explicit boundaries preserve future extraction without distributed transactions. |
| Local worker vs external broker | Simple first release vs distributed execution | Accept D-006 at topology level only: long-running ingestion executes locally, apart from request acceptance. |
| Application-owned contracts vs adapter-owned contracts | Mapping cost vs inward dependency control | Application owns semantic contracts so framework, provider, and persistence details stay outside Domain. |

## Components and Dependency Rules

| Boundary | Responsibility |
|---|---|
| API | HTTP, access control, validation, and composition; invokes Application. |
| Application | Use cases, orchestration, lifecycle policy, and boundary contracts; depends on Domain. |
| Domain | Identity, invariants, lifecycle, and provenance; no HTTP or persistence dependency. |
| Processing | Load, parse, normalize, and chunk through Application contracts. |
| Embeddings | Produce vectors under an embedding profile through Application contracts. |
| Persistence | Implement Application contracts using accepted PostgreSQL/pgvector. |
| Content Store | Retain and reopen immutable originals behind an opaque reference. |

Technical boundaries do not call each other or API; runtime coordination passes through Application.

```text
API -> Application -> Domain
          ^
          +-- Processing / Embeddings / Persistence / Content Store
          +-- Local background worker (long-running ingestion)
```

## Interfaces / Contracts

Contracts remain semantic: document/version persistence, operation lifecycle recording, immutable content access, processing, embeddings, profile compatibility, credential verification, and token issuance. Persistence schemas and persistence-level claim, lease, work-selection, idempotency, and retry-identity semantics are explicitly deferred. The local worker contract establishes only local background execution and lifecycle outcomes; it selects no algorithm, coordination mechanism, or storage design.

## Data Flow

```text
KeyId + secret -> token exchange -> verified identity -> short-lived token
upload -> deduplication decision -> Document/Version + pending Operation
local worker begins operation -> running -> load -> parse -> normalize
-> chunk -> embed -> index -> succeeded
stage failure -> failed(stage, trace)
failed + authorized explicit retry -> another traceable attempt
```

Equal `ExternalReference` and hash returns the existing document/version; a changed hash creates a new version; no external reference creates a new document, including across collections. Multi-collection search proceeds only for compatible embedding profiles. Terminal operations do not advance during normal processing. Failed ingestion is never retried automatically; only an authorized explicit retry starts another traceable attempt, while reuse or creation of Operation identity remains deferred.

## File Changes

| File | Action | Description |
|---|---|---|
| `openspec/changes/architecture-boundaries/design.md` | Modify | Align design scope with accepted D-003 through D-006 and revised specs. |
| `.proposals/07-decision-log.md` | Modify during apply | Mark D-003–D-006 and D-101, D-102, D-115 Accepted with their approved decisions. |

## Testing Strategy

| Layer | Planned validation |
|---|---|
| Architecture | Enforce dependency direction and the seven boundaries. |
| Unit | Lifecycle terminals, manual-only retry, deduplication, credential lifecycle, and profile compatibility. |
| Contract | Verify adapter behavior without fixing persistence coordination details. |
| Integration/E2E | Prove local background progression, ordered stages, traceable failure, token-derived identity, deduplication, and compatibility rejection without assuming claim, lease, selection, or retry identity. |

## Threat Matrix

N/A — the local worker is an internal architectural responsibility, not a routing, shell, subprocess, VCS/PR automation, executable-classification, or external process-integration boundary.

## Migration / Rollout

No migration or runtime rollout is required.

## Deferred Decisions and Non-goals

Defer HTTP errors/routes/headers, token format, hashing/scopes, idempotency window/storage, `ContentReference` format and production store, persistence-level claim/lease/work selection, retry identity, deletion, metadata, filters, vector indexing, reranking, hybrid search, multi-tenancy, external broker, OCR/vision, SDK generation, and remote ingestion to their designated phases. No implementation, automatic retry, microservices, distributed transactions, consumer coupling, server/SDK type sharing, or speculative abstraction is introduced.

## Open Questions

None blocking; deferred decisions retain their existing phase triggers.
