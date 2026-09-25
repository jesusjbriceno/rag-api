# Design: Client Administration Plane

## Technical Approach

Add internal `/api/v1/admin` routes. The external app validates Cloudflare Access, signs one assertion per API attempt, and separately HMAC-signs the request. Handlers mutate existing aggregates and append audit/idempotency state in one PostgreSQL transaction; bearer-token and owner-scoped RAG paths remain unchanged.

## Architecture Decisions

| Option | Tradeoff | Decision and rationale |
|---|---|---|
| Compact JWS JWT, RS256 | Larger than ES256; matches current primitives | Separate admin key ring/`kid`; claims: `iss`, `aud`, stable Cloudflare-derived `sub`, `app_id`, `iat`, `nbf`, `exp`, `jti`; 60-second lifetime, 30-second skew, no email/roles. App holds private keys; API holds public keys. Publish/switch/wait lifetime+skew/remove; emergency removal revokes a key. |
| HMAC-SHA256 machine proof | Canonicalization complexity; secret is not a bearer value | Sign method, path/query, body and assertion hashes, app/key IDs, timestamp, idempotency key. API accepts current/previous secret, constant-time compares, allows ±60 seconds, and binds `app_id`. Machine, assertion, and service-JWT keys MUST differ; rotate add/switch/remove. |
| Database replay plus idempotency | Adds small tables; survives replicas/restarts | Atomically reserve `(issuer,jti)` until expiry. Exact assertion replay is `401`; retries obtain a fresh assertion and reuse `Idempotency-Key`. `(app_id,key)` plus request fingerprint prevents duplicate mutation. Secret-producing retries return `409 secret_already_delivered`, never a recovered/new secret. |
| Dedicated policy/scheme | More middleware; prevents privilege confusion | Reject Cloudflare/proxy identity headers. Validate machine proof, JWS algorithm/`kid`/signature, issuer/audience/time, claims/app binding, then replay reservation. Create `AdminActor(ActorSubject,AppId)` only afterward; service JWTs never qualify. |
| Keyset cursor | Opaque cursor cannot support arbitrary jumps | Base64url versioned `(created_at,id)` cursor, ascending and exclusive; default/max page size 50/100. Invalid input is `400`; beyond-end is an empty page. |

## Data Flow

`Cloudflare → admin app validation → assertion + HMAC → admin policy → handler → PostgreSQL mutation + audit + idempotency commit → metadata/secret-once response`

## Interfaces / Contracts

Mutations require UUID `Idempotency-Key`; rotate/revoke require `If-Match: "v{version}"`. Metadata is client `{id,name,description,createdAt}` or credential `{id,clientId,keyId,description,version,state,createdAt,expiresAt,lastRotatedAt,revokedAt}`; state precedence is revoked/expired/active. Rotation preserves expiry, increments version, and invalidates old secrets and JWTs through existing `ICredentialStateValidator`; revocation is immediate.

| Endpoint | Request → success |
|---|---|
| `POST /clients` | `{name,description?}` → `201` client only |
| `GET /clients`, `GET /clients/{id}` | cursor page / client metadata → `200` |
| `POST /clients/{id}/credentials` | `{description?,expiresAt?}` → `201` metadata + `{secret}` once |
| `GET /clients/{id}/credentials`, `GET /credentials/{id}` | metadata only → `200` |
| `POST /credentials/{id}/rotate` | no body → `200` metadata + `{secret}` once |
| `POST /credentials/{id}/revoke` | no body → `200` metadata |
| `GET /audit` | cursor page → `200` |

Secret-free Problem Details include `code`/`traceId`: `400` validation/cursor, `401` trust/replay, `404` target, `409` duplicate/fingerprint/version/concurrency/idempotency, `415` content type, `429` rate limit, `500` generic. In-progress duplicates add `Retry-After`.

## Persistence and Boundaries

Add nullable `Description` to clients; add nullable `Description`/`LastRotatedAt` to credentials. Omit `IssuedByCredentialId` (actor is human) and `LastUsedAt` (avoids write-on-exchange); audit is authoritative. Add `admin_audit_events` (UUID, time, actor, app, action/outcome, targets, operation, allowlisted JSON), `admin_operations` (key/fingerprint/state/safe result), and `admin_assertion_replays` (issuer/JTI/app/expiry). Index audit `(OccurredAt,Id)` and target/time, unique operation `(AppId,IdempotencyKey)`, unique replay `(Issuer,Jti)`, and expiry. An API worker purges replay/operation rows and retained audit rows, first recording a system retention event; indefinite mode skips audit purge.

Domain enforces lifecycle; Application owns use cases/contracts; Infrastructure owns cryptography, persistence, purge; API owns policy/routes. Modify `ServiceClient.cs`, `ClientCredential.cs`, `IngestionDbContext.cs`, `InfrastructureServiceCollectionExtensions.cs`, `Program.cs`; create `AdminAuditEvent.cs`, `Rag.Application/Administration.cs`, `AdminAuthentication.cs`, `AdminRepository.cs`; plan an additive EF migration only. No collection/operator changes.

## Testing Strategy

| Layer | Coverage |
|---|---|
| Unit | Claim/order/key validation, HMAC canonicalization, domain expiry/state, cursor bounds, idempotency fingerprints, allowlisted audit. |
| Integration | Every route rejects public/JWT-only/raw-header/one-factor/forged/replayed calls; prove no-secret logs/audit/failures/lists; lifecycle, stale `If-Match`, races, retries, pagination, retention, restart persistence. |
| Regression/migration | Pre-migration clients still exchange tokens and retain owner isolation; existing `AuthenticationTests`, `AuthApiTests`, `ProtectedApiTests`, and `CredentialRepositoryTests` remain green. |

## Threat Matrix

HTTP routing triggered review; reference rows are N/A: documentation-like paths (no execution), Git repository selection, commit state, push state, and PR commands (no VCS/process automation). Route/auth adversarial tests are covered above.

## Migration / Rollout

Plan only: deploy additive schema/code disabled; provision trust; deploy the app privately; enable routes; verify isolation/audit/metrics, then operator access. Rollback disables routes/app and removes trust material while retaining audit tables; do not down-migrate.

Configuration names — API: `AdminPlane:Enabled`, `AdminAssertion:Issuer`, `AdminAssertion:Audience`, `AdminAssertion:ValidationKeys`, `AdminAppAuth:Apps`, `AdminAudit:RetentionMode`, `AdminAudit:RetentionDays`, `AdminOperations:RetentionHours`; app: `CloudflareAccess:TeamDomain`, `CloudflareAccess:Audience`, `AdminAssertion:Issuer`, `AdminAssertion:Audience`, `AdminAssertion:AppId`, `AdminAssertion:CurrentSigningKey`, `AdminApi:BaseUrl`, `AdminAppAuth:KeyId`, `AdminAppAuth:Secret`. Missing enabled configuration fails startup. Metrics/logs expose reason categories, action/outcome, audit/app IDs, status, latency, conflicts, replay, purge counts—never headers, assertions, bodies, secrets, hashes, or salts.

## Open Questions

None.
