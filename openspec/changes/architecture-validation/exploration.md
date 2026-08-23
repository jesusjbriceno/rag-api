# Exploration: Proposed Architecture Validation

## Purpose

Validate the documented .NET/ASP.NET Core + PostgreSQL/pgvector modular-monolith architecture before any implementation commitment. The repository contains no implementation, tests, CI, or commits. All architecture is treated as proposed rather than fact.

## Current State

The repository contains 11 proposal documents (`.proposals/00-README.md` through `.proposals/10-sdd-entry-prompt.md`) defining a comprehensive design for a consumer-agnostic RAG service. The OpenSpec bootstrap is initialized with `config.yaml`, `specs/`, and `changes/` directories. Engram contains the `sdd-init/rag-api` context.

**Repository state:**
- Git initialized with an unborn HEAD and no reachable commits
- No implementation files
- No tests, CI, formatter, linter, or type checker
- All architecture documented as Proposed or Deferred

**Proposed stack:**
- .NET / ASP.NET Core
- PostgreSQL with pgvector
- Npgsql
- EF Core (where useful)
- OpenAPI
- BackgroundService / hosted worker

**Proposed architecture:**
- Modular monolith
- Boundaries: API, Application, Domain/Core, Processing, Embeddings, Persistence, Content Store
- Background worker for async ingestion pipeline
- Content Store abstraction for original document retention

## Findings

### Strengths

1. **Clear consumer-agnostic design**: The service is explicitly designed to be reusable by any client (web apps, agents, n8n, scripts, CLI tools) without coupling to specific consumers.

2. **Strong separation of concerns**: The seven boundaries (API, Application, Domain, Processing, Embeddings, Persistence, Content Store) are well-defined with clear responsibilities and no overlap.

3. **Explicit decision tracking**: The Decision Log clearly distinguishes between Accepted, Proposed, and Deferred decisions. This prevents premature commitment and maintains design flexibility.

4. **Comprehensive guardrails**: 24 architectural guardrails (G-01 through G-24) prevent common pitfalls like premature optimization, speculative abstractions, and consumer coupling.

5. **Pragmatic vertical slice approach**: Starting with TXT format and a complete E2E flow (upload → parse → chunk → embed → search → provenance) validates the architecture before expanding scope.

6. **Content Store abstraction**: Separating `ContentReference` (internal retrieval) from `ExternalReference` (external provenance) enables reindexing without requiring clients to re-upload content.

7. **Appropriate async model**: Background worker with persisted Operation state handles long-running ingestion without blocking HTTP requests.

### Risks and Gaps

#### Critical Deferred Decisions

Three Deferred decisions should be resolved earlier than the current roadmap suggests:

1. **D-101 — Authentication mechanism**: The roadmap places Security at Fase 04, after HTTP Contract (Fase 03). However, the HTTP contract MUST specify authentication headers and flow. The auth mechanism (key+secret per request, token exchange, or HMAC signing) affects API design, SDK ergonomics, and security model. **Recommendation**: Resolve D-101 during or before Fase 03 (HTTP Contract), not Fase 04.

2. **D-102 — Deduplication rules**: The domain model mentions hash-based deduplication (same externalReference + same hash → idempotent result; same externalReference + different hash → new version), but the exact rules are Deferred. This affects the Document/DocumentVersion lifecycle, Operation semantics, and idempotency guarantees. **Recommendation**: Resolve D-102 before or during Fase 02 (Domain Model).

3. **D-115 — Multi-embedding-profile search**: The domain model introduces embedding profiles (Provider, Model, Dimensions, DistanceMetric, Version), and collections may be associated with different profiles. The search endpoint design must address whether cross-collection search is allowed when collections have incompatible dimensions/models. **Recommendation**: Resolve D-115 before Fase 03 (HTTP Contract) or explicitly document the constraint in the search endpoint design.

#### Missing Specifications

1. **Operation state machine**: The Operation concept is introduced with states (pending, running, succeeded, failed, cancelled?), but valid transitions and failure handling are not specified. What happens if embedding fails mid-process? Can an operation be retried? Does retry create a new operation or reuse the existing one?

2. **Error taxonomy**: The API contract mentions error responses with `code`, `message`, and `correlationId`, but no comprehensive error taxonomy exists. What are the domain-specific error codes? How do transient vs permanent failures differ?

3. **Background worker failure recovery**: The ingestion pipeline (load → parse → normalize → chunk → embed → index) can fail at any stage. The proposals mention "each phase must be able to report failure in a traceable way," but no recovery strategy is defined. Should failed operations be retried automatically? Should partial results be preserved?

4. **ContentReference format**: The Content Store abstraction is defined, but the format of `ContentReference` (the internal pointer to retrieve original content) is not specified. Is it a file path? A blob storage key? A database reference? This affects the Content Store implementation.

5. **Idempotency scope**: The API contract mentions `Idempotency-Key` header for upload, retry, reindex, and bulk operations, but the idempotency window, storage mechanism, and deduplication strategy are not defined.

#### Minor Concerns

1. **Processing vs Embeddings boundary**: These are listed as separate boundaries, but they're tightly coupled in the pipeline (chunk → embed). Keeping them separate is reasonable (different concerns: text processing vs vector generation), but they'll be deployed together and may share infrastructure (e.g., configuration, error handling).

2. **EF Core usage**: The proposal says "EF Core where useful," but the guardrails (G-07) prohibit using EF entities as public DTOs. This requires careful mapping strategy. Consider whether EF Core adds value for this project or whether raw SQL/Dapper would be simpler given the vector operations.

3. **JSONB usage**: The proposals mention JSONB for flexible metadata, but warn against storing relational data in JSONB. The boundary between "flexible metadata" and "relational data" needs clarification during persistence design.

## Stack Decision Validation

### .NET / ASP.NET Core

**Arguments in favor (from proposals):**
- Robust API framework with built-in dependency injection, middleware, and configuration
- Strong typing reduces runtime errors
- BackgroundService for hosted workers
- Excellent PostgreSQL/pgvector support via Npgsql
- Built-in OpenAPI/Swagger support
- NuGet ecosystem for SDK distribution
- Long-term maintainability and enterprise support

**Counterarguments considered:**
- **Python**: Superior AI/ML ecosystem (PyTorch, TensorFlow, transformers). However, this service CONSUMES AI capabilities (embeddings via abstracted provider) rather than implementing ML models. The AI ecosystem advantage is less relevant given the abstraction boundary.
- **Go**: Excellent for high-performance services, but less mature ORM ecosystem and less ergonomic for complex domain models. Go's simplicity is an advantage for microservices but less critical for a modular monolith.

**Verdict**: .NET is a sound choice for a stable, typed, maintainable service. The AI ecosystem argument for Python is valid but less critical given the embedding provider abstraction. The roadmap explicitly states this is "a stable service that consumes AI capabilities, not an ML laboratory."

**Risk**: .NET's AI/ML ecosystem is less mature than Python's. If the project later needs to implement custom embedding models, reranking, or advanced NLP locally (not via external API), Python interop or a separate Python service may be required. **Mitigation**: The embedding provider abstraction (D-010, Accepted) allows swapping implementations without changing the core. If needed, a Python sidecar service can be added later without breaking the architecture.

## Modular Monolith Validation

**Arguments in favor (from proposals):**
- Single cohesive capability (RAG service)
- Simple deployment (one process, one database)
- Lower operational cost (no service mesh, no distributed tracing, no consensus protocols)
- Transactional consistency (no distributed transactions)
- Background processing fits naturally (in-process worker)
- Clear internal boundaries enable future extraction if needed

**Counterarguments considered:**
- **Microservices**: Would add operational complexity (service discovery, load balancing, distributed tracing, consensus) without clear benefit for a single cohesive capability. The proposals explicitly reject this (D-005, G-10).
- **External broker (Kafka, RabbitMQ)**: Would add infrastructure complexity and operational overhead. The local background worker with persisted Operation state is sufficient for MVP. External broker only if load/reliability demands it (D-006, D-110, both Deferred).

**Verdict**: Modular monolith is the correct choice for MVP. The boundaries are well-defined and can be extracted later if needed. The proposals explicitly state "possibility of future extraction if a real need appears" (02-architecture-baseline.md, section 2).

**Risk**: If the service grows significantly (e.g., embedding becomes a bottleneck, or processing requires horizontal scaling), extracting modules from a monolith can be challenging. **Mitigation**: The clear boundaries and dependency rules (to be defined in Fase 01) make extraction feasible. The proposals explicitly avoid coupling modules to the monolith structure.

## Boundary Validation

### API Boundary
**Responsibilities**: HTTP, versioning, authentication, authorization, validation, status codes, OpenAPI, error contract, correlation IDs.
**Validation**: Clean. No RAG logic. Purely concerned with HTTP concerns.

### Application Boundary
**Responsibilities**: Use case orchestration (create client, create credential, create collection, upload document, retry operation, reindex, search, revoke credential).
**Validation**: Clean. No knowledge of ASP.NET, EF, or pgvector. Purely concerned with orchestrating domain operations.

### Domain/Core Boundary
**Responsibilities**: Core concepts (RagClient, Credential, Collection, CollectionGrant, Document, DocumentVersion, Chunk, Operation), lifecycle, invariants, provenance.
**Validation**: Clean. Contains stable business concepts. The proposals explicitly warn against modeling technical artifacts as domain entities (G-19).

### Processing Boundary
**Responsibilities**: Load → Parse → Normalize → Chunk.
**Validation**: Clean. Concerned with text transformation. The proposals emphasize pluggable strategies (IDocumentParser, IChunkingStrategy) but warn against speculative abstractions (G-18).

### Embeddings Boundary
**Responsibilities**: Text/chunk → Vector.
**Validation**: Clean. Concerned with vector generation. The embedding provider abstraction (D-010, Accepted) prevents coupling to specific providers/models.

### Persistence Boundary
**Responsibilities**: PostgreSQL, pgvector, migrations, mappings, SQL, indexes, JSONB.
**Validation**: Clean. Concerned with data storage and retrieval. The proposals warn against creating tables by conceptual symmetry (06-storage-and-processing.md, section 2).

### Content Store Boundary
**Responsibilities**: Immutable original content storage for DocumentVersion.
**Validation**: Clean. Abstracts physical storage (filesystem, S3, MinIO, Azure Blob). The separation of `ContentReference` (internal) from `ExternalReference` (external) is critical for reindexing (G-16, G-17).

**Potential Issue**: Processing and Embeddings are tightly coupled in the pipeline (chunk → embed). Keeping them separate is reasonable (different concerns), but they'll be deployed together and may share infrastructure. **Recommendation**: Keep them separate for now, but acknowledge they may evolve into a single "Processing" module with internal phases if the coupling increases.

## Roadmap Validation

The proposed roadmap sequence is logical:

1. **Fase 00 — Project Charter + Decision Log** ✅ (completed)
2. **Fase 01 — Architecture & Boundaries** (next)
3. **Fase 02 — Domain Model & Document Lifecycle**
4. **Fase 03 — HTTP Contract**
5. **Fase 04 — Security & Client Access**
6. **Fase 05 — Persistence & Storage Design**
7. **Fase 06 — Vertical Slice TXT**
8. **Fase 07 — Idempotency, Versioning & Reindexing**
9. **Fase 08 — Official .NET SDK**
10. **Fase 09 — Additional Formats**
11. **Fase 10 — Observability & Hardening**
12. **Fase 11 — Public Release**

**Issue**: Security (Fase 04) comes after HTTP Contract (Fase 03), but authentication is fundamental to the API. The HTTP contract must specify auth headers and flow.

**Recommendation**: Resolve D-101 (Authentication mechanism) before or during Fase 03, not Fase 04. Alternatively, move Security (Fase 04) before HTTP Contract (Fase 03). The current sequence risks designing the HTTP contract without knowing how authentication works.

## Contradictions

No explicit contradictions found between the proposal documents. The proposals are internally consistent and reinforce each other.

## Recommendations

### Immediate Actions (Before Fase 01)

1. **Resolve D-101 (Authentication mechanism)**: The auth mechanism affects HTTP contract design, SDK ergonomics, and security model. Choose between:
   - **Option A**: Key + Secret per request (simple, but secret travels on every request)
   - **Option B**: Key + Secret → short-lived token (reduces secret exposure, but adds complexity)
   - **Option C**: HMAC request signing (secret never transmitted, but complex canonicalization)
   
   **Recommendation**: Option B (token exchange) balances security and ergonomics. It reduces secret exposure, supports claims/scopes, and is compatible with standard OAuth2/OIDC flows. The complexity is manageable for a .NET service.

2. **Resolve D-102 (Deduplication rules)**: Define exact deduplication logic:
   - Same externalReference + same hash → idempotent result (return existing document/version)
   - Same externalReference + different hash → new DocumentVersion
   - No externalReference + same hash → ??? (new document? error? idempotent?)
   - Same content across collections → ??? (separate documents? shared document with multiple collection references?)
   
   **Recommendation**: 
   - Same externalReference + same hash → idempotent (return existing)
   - Same externalReference + different hash → new version
   - No externalReference + same hash → new document (no dedup without externalReference)
   - Same content across collections → separate documents (collections are independent scopes)

3. **Resolve D-115 (Multi-embedding-profile search)**: Decide whether cross-collection search is allowed when collections have incompatible embedding profiles (different dimensions/models).
   
   **Recommendation**: Disallow cross-collection search when collections have incompatible embedding profiles. The search endpoint should validate that all specified collections use compatible profiles. If incompatible, return an error. This simplifies the search implementation and avoids meaningless similarity comparisons.

### Fase 01 Deliverables

The next phase (Fase 01 — Architecture & Boundaries) should produce:

1. **Formal architecture specification**: Component diagram, dependency rules, module responsibilities.
2. **Ingestion lifecycle**: Detailed sequence diagram for TXT upload → search flow.
3. **Background worker model**: Operation state machine, failure handling, retry strategy.
4. **Stack decision**: Explicit justification for .NET/ASP.NET Core + PostgreSQL/pgvector.
5. **Updated Decision Log**: Resolve D-101, D-102, D-115 (or explicitly defer with justification).
6. **Module dependency rules**: Which modules can depend on which? (e.g., API → Application → Domain, Processing → Domain, Embeddings → Domain, Persistence → Domain).

### Deferred Decisions to Keep Open

The following Deferred decisions should remain open until more information is available:

- **D-103 (Delete semantics)**: Can remain deferred until deletion use cases are clarified.
- **D-104 (Metadata model/schema)**: Can remain deferred until metadata requirements are clearer.
- **D-105 (Filter language)**: Can remain deferred until search filtering requirements are clearer.
- **D-106 (Vector indexing strategy)**: Can remain deferred until load data exists (HNSW vs IVFFlat vs exact).
- **D-107 (Reranking)**: Can remain deferred until search quality requirements are clearer.
- **D-108 (Hybrid search)**: Can remain deferred until keyword search requirements emerge.
- **D-109 (Multi-tenancy)**: Can remain deferred until tenancy requirements are clarified.
- **D-110 (External queue/broker)**: Can remain deferred until load/reliability demands it.
- **D-111 (OCR provider/strategy)**: Can remain deferred until PDF/image support is needed.
- **D-112 (Vision provider/strategy)**: Can remain deferred until image analysis is needed.
- **D-113 (SDK generation strategy)**: Can remain deferred until API is stable.
- **D-114 (Production Content Store implementation)**: Can remain deferred until production deployment is planned.
- **D-116 (Operation vs Ingestion resource)**: Minor, can remain deferred.

## Conclusion

The proposed architecture is **sound and ready for Fase 01**. The modular monolith approach with .NET/ASP.NET Core + PostgreSQL/pgvector is appropriate for the stated goals. The boundaries are well-defined, the guardrails are strong, and the roadmap is logical.

Three Deferred decisions (D-101, D-102, D-115) should be resolved earlier than the current roadmap suggests to avoid blocking subsequent phases. The Security phase (Fase 04) should move before or overlap with the HTTP Contract phase (Fase 03) to ensure authentication is integrated into the API design from the start.

The architecture is consumer-agnostic, extensible, and pragmatic. It avoids premature complexity (no microservices, no external broker, no speculative abstractions) while maintaining clear boundaries for future evolution.

## Ready for Proposal

**Yes**. The proposed architecture is validated and ready for Fase 01 (Architecture & Boundaries). The orchestrator should inform the user that:

1. The proposed architecture is sound and internally consistent.
2. Three Deferred decisions (auth mechanism, dedup rules, multi-embedding-profile search) should be resolved before or during Fase 01-03.
3. The Security phase should move earlier in the roadmap to integrate auth into the HTTP contract.
4. The next step is Fase 01 (Architecture & Boundaries), which will formalize the architecture, define module dependencies, and specify the ingestion lifecycle.
