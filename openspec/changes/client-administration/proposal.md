# Proposal: Client Administration Plane

## Intent

Introduce an internal-only administrative plane for integration client and credential lifecycle. Today all administration is CLI-only (`Rag.Operator`), with no audit trail, no per-person attribution, and no runtime credential governance. This enables authorized operators to manage clients, rotate/revoke credentials, and inspect audit logs through a minimal admin app backed by Cloudflare Zero Trust — without exposing admin surface publicly or introducing a client-usable root credential.

**Amendment**: Complete five scenarios identified by final verification as lacking runtime coverage: two Cloudflare assertion-issuance scenarios (admin app missing), two migration compatibility scenarios (pre-admin data coexistence), and one post-expiry credential exchange rejection scenario.

## Scope

### In Scope
- Minimal admin app on Cloudflare-protected route requiring group membership
- Cloudflare Access JWT validation (RS256) and short-lived assertion issuer (≤5 min expiry)
- API verification of signed internal assertions (issuer, audience, expiry, signature, subject, replay)
- Deployment secret for machine authentication
- Admin endpoints: client create/list/detail, credential issue/list/inspect/rotate/revoke, paginated audit
- Append-only audit with verified human `ActorSubject` (opaque Cloudflare subject; no email)
- Compatibility with existing `ServiceClient`, `ClientCredential`, token exchange, collection ownership
- Runtime coverage for five uncovered scenarios:
  - Verified person receives assertion (admin-assertion-auth)
  - Invalid Cloudflare credential rejected (admin-assertion-auth)
  - Existing client usable after migration (admin-client-lifecycle)
  - Existing data and audit coexist (admin-audit-trail)
  - Expired credential denied (admin-credential-lifecycle)
- End-to-end Cloudflare-to-emitter-to-admin-API integration test

### Out of Scope
- Collection owner assignment or shared collection access
- Public admin surface or self-service onboarding
- Direct SQL owner assignments, PostgreSQL/llama.cpp access for clients
- CLI break-glass tooling
- Cloudflare proxying/forwarding (assertion is sole identity carrier)
- Email persistence in emitter or audit logs
- Redesign of admin API or machine proof scheme

## Capabilities

### New
- `admin-assertion-auth`: Cloudflare validation, assertion issuance/verification, machine auth
- `admin-client-lifecycle`: Create, list, inspect integration clients
- `admin-credential-lifecycle`: Issue, list, inspect, rotate, revoke credentials
- `admin-audit-trail`: Paginated append-only audit with `ActorSubject` attribution

### Modified
None

## Approach

**Trust boundary**: Cloudflare Access authenticates people to admin app (group membership required). App validates Cloudflare JWT (RS256 via JWKS or static test key), extracts opaque subject, issues short-lived RS256 assertion (≤5 min). API verifies assertion claims; never trusts raw headers. Deployment secret authenticates app as machine.

**Emitter audit**: Admin app logs only opaque Cloudflare subject; no email persistence.

**Audit model**: Verified `ActorSubject` from assertion; app identifier separate. Never store secrets, hashes, tokens, assertions, credentials, or credential-bearing bodies.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Rag.Api/Program.cs` | Modified | Admin endpoint group with assertion auth |
| `src/Rag.Application/` | Modified | Admin handlers, audit writer |
| `src/Rag.Domain/ClientCredential.cs` | Modified | Metadata fields |
| `src/Rag.Infrastructure/Migrations/` | New | Credential metadata, audit table |
| `src/Rag.AdminApp/` | New | Cloudflare JWT validation, RS256 assertion issuance |
| `tests/Rag.IntegrationTests/` | Modified | Migration compatibility, expiry, e2e tests |
| `tests/Rag.AdminApp.Tests/` | New | Cloudflare validator and issuer unit tests |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Assertion key compromise | Medium | Short-lived (≤5 min), key rotation, separate deployment secret |
| Deployment secret leak | Low | Typed schema, no-secret tests, log redaction |
| Admin app down | Low | Stateless; API independent |
| Cloudflare JWKS dependency | Medium | Configurable validator (JWKS or static key); mock for tests |

## Rollback Plan

- Admin endpoints additive; removal reverts to CLI-only
- Audit table additive; droppable
- Credential metadata nullable; droppable
- Assertion policy removable; reverts to JWT-only
- Deployment secret rotatable
- Admin app separate; removal does not affect API

## Dependencies

- Cloudflare Zero Trust with Access policy and group membership
- Deployment secret (secret manager)
- Assertion signing key
- Cloudflare team domain JWKS endpoint (or static test key)

## Success Criteria

- [ ] Operators authenticate via Cloudflare Access (group enforced)
- [ ] Admin app manages clients and credentials
- [ ] Operations recorded with verified `ActorSubject` (opaque subject; no email)
- [ ] Audit contains no secret material
- [ ] Existing domain/exchange/ownership compatible
- [ ] Deployment secret absent from artifacts/logs/responses
- [ ] API rejects raw Cloudflare headers
- [ ] Verified person receives signed assertion (≤5 min)
- [ ] Invalid Cloudflare credential rejected
- [ ] Pre-admin client usable after migration
- [ ] Pre-admin data and audit coexist
- [ ] Post-expiry exchange rejected; active credentials usable
- [ ] End-to-end Cloudflare-to-emitter-to-API flow passes
