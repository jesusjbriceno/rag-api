# Exploration: Client Administration Plane

## Purpose

Investigate options, constraints, and risks for introducing a maintainable client-administration plane into the rag-api modular monolith. This exploration covers root bootstrap, authenticated lifecycle administration, per-integration credentials, secret-once display, collection ownership governance, audit trail, and internal-only surface — without modifying any application code.

## Current State

### Confirmed from source (CodeGraph-verified)

| Concept | Location | Behavior |
|---------|----------|----------|
| `ServiceClient` | `src/Rag.Domain/ServiceClient.cs` | Immutable after construction: `Id`, `Name` (unique, ≤200 chars), `CreatedAt`. No roles, no claims, no status. |
| `ClientCredential` | `src/Rag.Domain/ClientCredential.cs` | Belongs to one `ServiceClientId`. Supports `Rotate` (version increment, new hash/salt), `Revoke` (status → Revoked, `RevokedAt`). `IsActiveAt(now)` checks status + expiry. Optimistic concurrency via `Version`. |
| `CredentialOperator` | `src/Rag.Application/Authentication.cs:84` | `IssueAsync(name, expiresAt)` creates ServiceClient + first credential atomically. `RotateAsync(keyId)` and `RevokeAsync(keyId)` operate on existing credentials. No list, no status, no multi-credential per client. |
| `CredentialExchangeHandler` | `src/Rag.Application/Authentication.cs:54` | KeyId + Secret → short-lived JWT (15 min). Timing-safe failure paths (dummy verify). |
| JWT claims | `src/Rag.Infrastructure/JwtAuthentication.cs:136` | `client_id`, `credential_id`, `credential_version`. No role/scope/admin claims. |
| `Collection` | `src/Rag.Domain/Collection.cs` | `ServiceClientId` is non-nullable. Unique index on `(ServiceClientId, lower(btrim(Name)))`. |
| Collection ownership | `src/Rag.Application/CollectionOwnership.cs` | `ListUnownedAsync` + `AssignOwnerAsync` (one-way, deliberate, only when `ServiceClientId IS NULL`). |
| Enforcement migration | `20260824150100_EnforceCollectionOwnership` | Blocks if any unowned collections exist. Requires `Rag.Operator collections list-unowned` + `assign-owner` first. |
| API surface | `src/Rag.Api/Program.cs` | `POST /api/v1/auth/token` (anonymous, rate-limited). All other routes require JWT bearer. No admin endpoints. |
| Operator CLI | `src/Rag.Operator/Program.cs` | `issue <name>`, `rotate <keyId>`, `revoke <keyId>`, `collections list-unowned`, `collections assign-owner <colId> <clientId>`, `migrate`. Direct DB access. |
| Secret hashing | `src/Rag.Infrastructure/CredentialSecurity.cs` | Argon2id, 32-byte hash, 16-byte salt, version 1. Constant-time verify. Dummy verify on failure paths. |
| DI wiring | `src/Rag.Infrastructure/InfrastructureServiceCollectionExtensions.cs` | `ICredentialRepository` → `CredentialRepository` (scoped). `ICredentialStateValidator` → same instance. `CredentialOperator` scoped. |
| Tests | `tests/Rag.UnitTests/AuthenticationTests.cs`, `tests/Rag.IntegrationTests/CredentialRepositoryTests.cs`, `tests/Rag.IntegrationTests/ProtectedApiTests.cs`, `tests/Rag.IntegrationTests/AuthApiTests.cs` | Cover exchange, rotation, revocation, concurrency, owner-scoped collections, rate limiting, anonymous health. |

### What does NOT exist today

- No administrative API endpoints (all admin is CLI-only via `Rag.Operator`).
- No concept of "root" or "admin" role/claim in JWT or domain.
- No multiple credentials per `ServiceClient` (issue creates both client + first credential atomically).
- No credential listing, status query, or metadata-only views.
- No audit trail (no table, no events, no logging of admin actions).
- No collection creation by admin for a specific owner (clients create their own collections via `POST /api/v1/collections`).
- No credential expiration enforcement beyond `IsActiveAt` check at exchange time.
- No recovery mechanism for lost secrets (only rotation, which requires the old credential's KeyId).

### Foundational constraints (from `.proposals/`)

- **G-04**: n8n is a normal client. No special treatment.
- **G-08**: Never store API secrets in clear.
- **G-21**: Never log secrets, auth headers, document bodies, or sensitive queries.
- **G-24**: Project must remain publishable (no private URLs, keys, internal names).
- **D-101**: Token exchange accepted (KeyId + secret → short-lived token).
- **05-security-baseline §13**: Admin bootstrap mechanism and audit retention are explicitly Deferred.
- **05-security-baseline §5**: Authorization scopes conceptually include `admin` but are not implemented.

## Affected Areas

| File / Module | Why affected |
|---------------|-------------|
| `src/Rag.Domain/ServiceClient.cs` | May need status, kind/role, or metadata to distinguish root from integration clients. |
| `src/Rag.Domain/ClientCredential.cs` | May need `IssuedBy`, `Description`, or `LastUsedAt` (already in security baseline §2 as deferred). |
| `src/Rag.Application/Authentication.cs` | `CredentialOperator` must grow list/status/rotate-by-id/revoke-by-id. New admin use-case handlers. |
| `src/Rag.Application/CollectionOwnership.cs` | May need admin-scoped collection creation or transfer. |
| `src/Rag.Infrastructure/JwtAuthentication.cs` | JWT claims must carry role/scope for authorization decisions. |
| `src/Rag.Infrastructure/CredentialRepository.cs` | New queries: list-by-client, list-all-clients, find-by-id-with-client, audit writes. |
| `src/Rag.Infrastructure/IngestionDbContext.cs` | New entities/tables: audit log, possibly admin sessions. |
| `src/Rag.Infrastructure/Migrations/` | New migration(s) for audit table, credential metadata extensions, service-client kind. |
| `src/Rag.Api/Program.cs` | New admin endpoint group with distinct authorization policy. |
| `src/Rag.Operator/Program.cs` | Bootstrap command for root; may coexist with admin API or be deprecated for non-bootstrap ops. |
| `src/Rag.Infrastructure/InfrastructureServiceCollectionExtensions.cs` | New DI registrations for admin handlers, audit writer, authorization policies. |
| `tests/Rag.UnitTests/AuthenticationTests.cs` | New unit tests for admin handlers, claim extraction, authorization. |
| `tests/Rag.IntegrationTests/` | New integration tests for admin API, audit persistence, migration compatibility. |

## Approaches

### 1. Root Bootstrap via Environment Secret + Admin JWT Claims

**Description**: A deployment-time environment variable (`ADMIN_BOOTSTRAP_SECRET`) is hashed and stored as a special `ServiceClient` with `Kind = Root`. The bootstrap command (`Rag.Operator bootstrap`) creates this root client + first admin credential. Admin API endpoints require a JWT with `role: admin` claim. The root credential is the only one that can create/revoke/rotate other clients' credentials.

- **Pros**:
  - Clear separation: root is a deployment concern, not a client concern.
  - Reuses existing `ServiceClient` + `ClientCredential` domain (minimal schema change).
  - Admin API is authenticated the same way as client API (JWT bearer), just with different claims.
  - Bootstrap is a one-time CLI operation, consistent with existing `Rag.Operator` pattern.
- **Cons**:
  - Root is stored in the same tables as integration clients — requires careful query/policy isolation.
  - If root credential is lost, recovery requires direct DB access or a new bootstrap (which must handle existing root).
  - `Kind` column addition is a migration that touches all rows.
- **Effort**: Medium

### 2. Separate Admin Table + Independent Auth Path

**Description**: A separate `admin_accounts` table with its own credential store, independent of `service_clients` / `client_credentials`. Admin API uses a different authentication scheme (e.g., separate JWT issuer/audience, or API key header). Root bootstrap creates the first admin account.

- **Pros**:
  - Complete isolation: admin and client credentials never share tables or code paths.
  - Easier to audit and reason about — admin actions are always from admin accounts.
  - No risk of confusing root with integration clients in queries.
- **Cons**:
  - Significant new domain/infrastructure code (new entities, repositories, auth scheme).
  - Two parallel credential systems to maintain, hash, rotate, and secure.
  - Duplicates much of the existing `ClientCredential` logic.
  - Harder to justify for a modular monolith that explicitly avoids premature distribution.
- **Effort**: High

### 3. Root as a Claim on Existing ServiceClient + Admin Credential Metadata

**Description**: Add a `Kind` enum to `ServiceClient` (`Integration`, `Root`). Add metadata to `ClientCredential` (`Description`, `IssuedByCredentialId`, `LastUsedAt`). Root bootstrap creates a `ServiceClient` with `Kind = Root` and its first credential. Admin API endpoints check `kind == Root` from JWT claims. All credential operations (create, rotate, revoke, list) go through admin API, with audit logging.

- **Pros**:
  - Extends existing domain naturally — `Kind` is a single column, `IssuedByCredentialId` enables lineage.
  - Admin API reuses JWT auth; authorization is a claim check.
  - Audit trail is a new table, written by admin handlers — clean separation of concerns.
  - Supports multiple credentials per client (admin creates them, shows secret once).
  - Collection ownership governance: admin can create collections for a specific owner, or assign owners to unowned collections (replacing CLI).
- **Cons**:
  - `IssuedByCredentialId` creates a self-referential FK on `client_credentials` — must handle root's first credential (null `IssuedByCredentialId`).
  - Migration must backfill `Kind = Integration` for all existing `ServiceClient` rows.
  - Admin API surface is larger — more endpoints to secure and test.
- **Effort**: Medium-High

## Recommendation

**Approach 3** (Root as Kind + Admin Credential Metadata + Admin API) is recommended, with these specifics:

### Root Bootstrap Model

- `ServiceClient.Kind` enum: `Integration` (default), `Root`.
- `Rag.Operator bootstrap` command: checks no root exists, creates `ServiceClient(Kind=Root)` + first `ClientCredential` with `IssuedByCredentialId = null`. Prints secret once.
- Root bootstrap secret is generated by the operator, NOT provided by the user. The operator prints it once; the deployer stores it in their secret manager.
- If a root already exists, bootstrap refuses and prints "Root already exists. Use admin API to manage credentials."

### Claims / Roles Separation

| Actor | JWT Claims | Can Do |
|-------|-----------|--------|
| Root credential | `client_id`, `credential_id`, `credential_version`, `role: admin` | Create/revoke/rotate/list any client's credentials. Create collections for any owner. Assign owners to unowned collections. View audit log. |
| Integration credential | `client_id`, `credential_id`, `credential_version` (no `role` claim) | Create collections (owned by self). Ingest into own collections. Search own collections. View own operation status. |

- `role` claim is optional. Absence means integration client.
- Authorization policy: `RequireClaim("role", "admin")` for admin endpoints. Fallback policy remains `RequireAuthenticatedUser`.

### Endpoint / Authorization Shape

```
POST   /api/v1/admin/clients                          [admin]  — create integration client + first credential
GET    /api/v1/admin/clients                          [admin]  — list clients (no secrets)
GET    /api/v1/admin/clients/{clientId}               [admin]  — client detail + credential metadata (no secrets)
POST   /api/v1/admin/clients/{clientId}/credentials   [admin]  — issue additional credential for existing client
GET    /api/v1/admin/clients/{clientId}/credentials   [admin]  — list credential metadata (no secrets)
POST   /api/v1/admin/credentials/{credentialId}/rotate  [admin]  — rotate specific credential
POST   /api/v1/admin/credentials/{credentialId}/revoke  [admin]  — revoke specific credential
GET    /api/v1/admin/credentials/{credentialId}       [admin]  — credential status/metadata
POST   /api/v1/admin/collections                      [admin]  — create collection for a specific owner
POST   /api/v1/admin/collections/{id}/assign-owner    [admin]  — assign owner to unowned collection
GET    /api/v1/admin/collections/unowned              [admin]  — list unowned collections
GET    /api/v1/admin/audit                            [admin]  — audit log (paginated)
GET    /api/v1/admin/status                           [admin]  — system status (client count, credential count, etc.)
```

- All admin endpoints require `role: admin` claim.
- Admin endpoints NEVER return secret material. Secrets are shown only at creation/rotation time, in the response body, once.
- Rate limiting: admin endpoints get a separate, stricter rate limit policy.

### Direct Owner Collection Creation vs Root Assignment

- **Admin creates collection for owner**: `POST /api/v1/admin/collections` with `{ "name": "facturas_material", "owner_client_id": "<guid>" }`. The collection is created with `ServiceClientId = owner_client_id`. This is the primary path for governance.
- **Root assigns owner to unowned**: `POST /api/v1/admin/collections/{id}/assign-owner` with `{ "owner_client_id": "<guid>" }`. This replaces the CLI `collections assign-owner` for runtime use. The CLI command remains for bootstrap/migration scenarios.
- **Integration client creates own collection**: `POST /api/v1/collections` (existing) continues to work. The collection is owned by the authenticated client.

### Rotation / Revocation / Expiration / Recovery

- **Rotation**: Admin rotates a specific credential by ID. New secret is generated, shown once. Old secret is immediately invalid (version increment).
- **Revocation**: Admin revokes a specific credential by ID. Status → Revoked, `RevokedAt` set. Immediate effect (next token exchange fails `IsActiveAt`).
- **Expiration**: Optional `expires_at` on credential creation. `IsActiveAt` already checks this.
- **Recovery**: If root credential is lost, the deployer must run `Rag.Operator bootstrap` again. Bootstrap detects existing root and offers "rotate root credential" mode (requires DB access or a recovery token). Alternatively, a new migration/CLI command `Rag.Operator root rotate` can be added later. For now, bootstrap refusal + manual DB intervention is acceptable.

### Audit Retention and Never-Log Fields

- **Audit table**: `admin_audit_log` with columns: `Id` (uuid), `OccurredAt` (timestamptz), `ActorCredentialId` (uuid, nullable for bootstrap), `ActorClientId` (uuid, nullable), `Action` (varchar, e.g., "client.create", "credential.rotate", "credential.revoke", "collection.assign_owner"), `TargetClientId` (uuid, nullable), `TargetCredentialId` (uuid, nullable), `TargetCollectionId` (uuid, nullable), `Metadata` (jsonb, e.g., `{ "client_name": "gest_contable" }`).
- **Retention**: No automatic deletion. Audit log is append-only. Retention policy is a deployment concern (DBA manages tablespace/partitioning).
- **Never-log fields**: Secret material (plaintext secrets, hashes, salts) MUST NOT appear in audit metadata, application logs, or error responses. The audit log records IDs and names, never secrets.
- **Structured logging**: Admin API requests log `ActorCredentialId`, `Action`, `TargetClientId`, HTTP status. Never log `Authorization` header, request body secrets, or response secrets.

### Compatible Migration Approach

1. **Migration 1**: Add `Kind` column to `service_clients` (varchar, default `'Integration'`, not null). Add check constraint `CK_service_clients_Kind_valid` (`"Kind" IN ('Integration', 'Root')`). Backfill: all existing rows get `'Integration'`.
2. **Migration 2**: Add `Description` (varchar(500), nullable), `IssuedByCredentialId` (uuid, nullable, FK → `client_credentials.Id`), `LastUsedAt` (timestamptz, nullable) to `client_credentials`.
3. **Migration 3**: Create `admin_audit_log` table.
4. **Migration 4**: (Optional) Add unique index on `client_credentials(ServiceClientId, KeyId)` if multi-credential per client is desired (currently KeyId is globally unique, which already supports multiple credentials per client).

- All migrations are additive. No data loss. Existing `CredentialOperator.IssueAsync` continues to work (creates `Kind = Integration` client + credential with `IssuedByCredentialId = null`).
- The `EnforceCollectionOwnership` migration already ran. No ownership changes needed.

### Authorization / Isolation / Revocation Test Strategy

- **Unit tests**:
  - Admin handler creates client + credential, returns secret once, audit entry written.
  - Admin handler lists clients without secrets.
  - Admin handler rotates credential, old version invalid, new secret returned once.
  - Admin handler revokes credential, subsequent exchange fails.
  - Integration client JWT has no `role` claim → admin endpoints return 403.
  - Audit log never contains secret material (assert on metadata JSON).
- **Integration tests**:
  - Admin API E2E: bootstrap → create client → create credential → rotate → revoke → verify exchange fails.
  - Admin creates collection for owner → owner can ingest/search → foreign client cannot.
  - Audit log persists across operations, queryable with pagination.
  - Migration compatibility: existing data survives migration, existing `CredentialOperator.IssueAsync` still works.
  - Rate limiting on admin endpoints.
  - Concurrent rotation/revocation surfaces `CredentialConcurrencyException` → 409/500 with retry instruction.

## Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| Root credential loss locks out admin API | High | Document bootstrap recovery procedure. Consider adding `Rag.Operator root rotate` CLI command in a follow-up. For now, manual DB intervention is the escape hatch. |
| Admin API surface is large and must be secured correctly | Medium | Start with minimal admin endpoints (client CRUD, credential CRUD, audit read). Defer collection admin to a follow-up if needed. Use policy-based authorization, not ad-hoc checks. |
| Audit log grows unbounded | Low | Append-only by design. Retention is a deployment concern. Document partitioning strategy for large deployments. |
| Migration adds columns to hot tables | Low | All additions are nullable or have defaults. No table rewrite expected for `Kind` (varchar with default). `IssuedByCredentialId` is nullable FK. |
| Confusing root with integration clients in queries | Medium | `Kind` column + check constraint. Admin queries always filter `Kind = 'Root'` or `Kind = 'Integration'`. Unit tests assert isolation. |
| Secret material leaks via audit log or error responses | High | Audit metadata schema is typed — no free-form secret fields. Error responses use problem details with no secret material. Integration tests assert absence of secrets in logs/audit. |
| Existing `CredentialOperator.IssueAsync` breaks | Low | Migration backfills `Kind = 'Integration'`. `IssuedByCredentialId` is nullable. Existing code path unchanged. |

## Open Questions for Proposal

1. Should `ServiceClient.Kind` be an enum in C# mapped to varchar, or a string property with validation? (Recommendation: enum mapped to varchar, consistent with `CredentialStatus`.)
2. Should the audit log be a separate table, or use an event-sourced approach? (Recommendation: separate table. Event sourcing is premature for this monolith.)
3. Should admin API be on a separate port/path prefix, or same API with policy-based auth? (Recommendation: same API, `/api/v1/admin/` prefix, policy-based auth. Simpler deployment, clear URL boundary.)
4. Should `LastUsedAt` be updated on every token exchange, or only on admin operations? (Recommendation: every token exchange — requires `ICredentialRepository` to expose `UpdateLastUsedAsync`. This is a performance consideration — batch updates or async write.)
5. Should the root bootstrap command accept an optional `--description` for the first credential? (Recommendation: yes, for operational clarity.)

## Ready for Proposal

**Yes.** The exploration has identified:
- The recommended approach (Kind-based root + admin credential metadata + admin API).
- The migration path (additive, compatible).
- The endpoint shape and authorization model.
- The audit trail design.
- The test strategy.
- The risks and mitigations.

The orchestrator should proceed to `sdd-propose` to formalize the intent, scope, and rollback plan.
