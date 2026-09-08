## Exploration: Client Administration — Scope Completion (5 Uncovered Scenarios)

### Current State

The `client-administration` change has 34/34 tasks complete across Phases 1–5. Build and all 151 tests pass (95 unit + 56 Testcontainers integration). The final verify report (`verdict: fail`) identified **5 required scenarios without passing runtime coverage**:

| # | Spec | Scenario | Root cause |
|---|------|----------|------------|
| 1 | admin-assertion-auth | Verified person receives an assertion | No admin app implementation exists |
| 2 | admin-assertion-auth | Invalid Cloudflare credential is rejected | No admin app implementation exists |
| 3 | admin-client-lifecycle | Existing client remains usable after migration | No migration compatibility test |
| 4 | admin-audit-trail | Existing data and new audit coexist | No migration compatibility test |
| 5 | admin-credential-lifecycle | Expired credential is denied | No post-expiry exchange test |

The API-side trust boundary (assertion validation, machine proof, replay, handler lifecycle, audit, retention, observability) is fully implemented and tested. The missing pieces are: (a) the external admin app that validates Cloudflare and issues assertions, and (b) three focused integration tests.

### Affected Areas

- `openspec/changes/client-administration/specs/admin-assertion-auth/spec.md` — defines the two Cloudflare-facing scenarios; no code satisfies them
- `openspec/changes/client-administration/specs/admin-client-lifecycle/spec.md` — "Existing client remains usable after migration" scenario; no test
- `openspec/changes/client-administration/specs/admin-audit-trail/spec.md` — "Existing data and new audit coexist" scenario; no test
- `openspec/changes/client-administration/specs/admin-credential-lifecycle/spec.md` — "Expired credential is denied" scenario; no test
- `src/Rag.Infrastructure/Migrations/20260827000000_AddAdminPlane.cs` — additive migration under test (no changes needed; it's purely additive)
- `src/Rag.Application/Authentication.cs` — `CredentialExchangeHandler.ExchangeAsync` already checks `IsActiveAt(now)` which rejects expired credentials; no code change needed
- `src/Rag.Domain/ClientCredential.cs` — `IsActiveAt` already implements `Status == Active && (ExpiresAt is null || ExpiresAt > now)`; no code change needed
- `tests/Rag.IntegrationTests/AdminApiTests.cs` — existing admin integration test file; expiry test could extend it or live in a new file
- `tests/Rag.IntegrationTests/ProtectedApiTests.cs` — existing pre-admin exchange test patterns (reference for migration compatibility test structure)
- `docs/deployment/client-administration.md` — documents the admin app as "packaging TBD" with design-specified config names

### Approaches

#### 1. Migration compatibility + expiry rejection (scenarios 3, 4, 5) — test-only

**Approach**: Add focused integration tests without any production code changes.

- **Migration compatibility** (scenarios 3 & 4): New test class `AdminMigrationCompatibilityTests` that (a) creates a `ServiceClient` + `ClientCredential` via `CredentialOperator` (pre-admin path), (b) runs the admin migration, (c) exchanges the pre-existing credential and proves the JWT is issued, (d) creates a new client via the admin API, (e) reads audit events and proves both the pre-existing exchange and the new admin audit event coexist. The migration is additive (nullable columns + new tables), so pre-existing rows are unaffected. The test uses the existing `PostgreSqlFixture` + `AdminApiFactory` infrastructure.

- **Expiry rejection** (scenario 5): New test that (a) creates a client+credential via admin API with a short future expiry, (b) backdates `ExpiresAt` to the past via direct SQL (`UPDATE client_credentials SET "ExpiresAt" = now() - interval '1 hour'`), (c) attempts token exchange → expects 401, (d) creates a second credential (no expiry) for the same client, (e) exchanges the active credential → expects 200. This proves `IsActiveAt` enforcement at the exchange boundary without needing clock manipulation.

- **Pros**: Zero production risk; pure test additions; exercises real DB + real exchange handler; reuses existing test infrastructure.
- **Cons**: None significant. The backdate-via-SQL approach is a standard integration test pattern.
- **Effort**: Low (~150–200 lines of test code).

#### 2. Minimal admin app (scenarios 1 & 2) — new project

**Approach**: Create a minimal admin app as a new project in the solution (`src/Rag.AdminApp/`) that validates Cloudflare Access JWTs and issues RS256-signed assertions.

The app would contain:
- `CloudflareAccessValidator` — validates Cloudflare Access JWTs using the team domain's JWKS endpoint (or a configured public key for testing). Extracts the verified subject claim.
- `AdminAssertionIssuer` — signs short-lived RS256 assertions with the required claims (`sub`, `app_id`, `jti`, `iss`, `aud`, `iat`, `nbf`, `exp`) using the configured private signing key.
- `Program.cs` — minimal API that exposes a single endpoint (e.g., `POST /assertion`) that validates the Cloudflare credential and returns a signed assertion.

Tests:
- Unit tests for `CloudflareAccessValidator` (valid token → subject extracted; expired/invalid/forged → rejected).
- Unit tests for `AdminAssertionIssuer` (issued assertion contains all required claims; short-lived; signed with correct key).
- Integration test that starts the admin app, presents a valid Cloudflare credential, receives an assertion, and verifies the assertion's claims.

- **Pros**: Satisfies the spec's requirement for a Cloudflare-facing admin app; provides a real deployable artifact; the assertion issuance logic is testable in isolation.
- **Cons**: Introduces a new project with Cloudflare JWKS dependency; the integration test requires either a mock Cloudflare JWKS endpoint or a test-mode validator; adds deployment surface area.
- **Effort**: Medium (~250–350 lines for the app + tests).

#### 3. Admin app test harness (scenarios 1 & 2) — test-only simulation

**Approach**: Instead of building the full admin app, create a test harness within the integration test project that simulates the admin app's behavior (Cloudflare validation + assertion issuance) and exercises the full flow against the API.

The test harness would:
- Generate a mock Cloudflare Access JWT (signed with a test key).
- Validate it using a test-mode validator (configurable to accept/reject).
- Issue a signed assertion using the same `AdminAssertionIssuer` logic that the real app would use.
- Send the assertion + machine proof to the API.

- **Pros**: No new project; stays within the existing test infrastructure; proves the assertion issuance flow end-to-end.
- **Cons**: Does not produce a deployable admin app; the spec says the app MUST exist, not just be testable; defers the app implementation to a future change.
- **Effort**: Low-Medium (~150–200 lines of test code).

### Recommendation

**Combine approaches 1 and 2**: Build the minimal admin app (approach 2) AND add the migration compatibility + expiry tests (approach 1).

Rationale:
- The spec explicitly requires the admin app to exist ("Cloudflare Access MUST authenticate persons only to the minimal admin app"). A test harness (approach 3) does not satisfy this requirement.
- The migration compatibility and expiry tests (approach 1) are pure test additions with zero production risk and directly address 3 of the 5 uncovered scenarios.
- The admin app (approach 2) addresses the remaining 2 scenarios and produces a real deployable artifact that the design and deployment docs reference.

**Work Unit breakdown**:

| Unit | Scope | Scenarios | Est. lines | Files |
|------|-------|-----------|------------|-------|
| A | Migration compatibility + expiry rejection tests | 3, 4, 5 | ~150–200 | `tests/Rag.IntegrationTests/AdminMigrationCompatibilityTests.cs` (new), `tests/Rag.IntegrationTests/AdminApiTests.cs` (modified) |
| B | Minimal admin app (Cloudflare validation + assertion issuance) | 1, 2 | ~250–350 | `src/Rag.AdminApp/Program.cs` (new), `src/Rag.AdminApp/CloudflareAccessValidator.cs` (new), `src/Rag.AdminApp/AdminAssertionIssuer.cs` (new), `tests/Rag.AdminApp.Tests/CloudflareAccessValidatorTests.cs` (new), `tests/Rag.AdminApp.Tests/AdminAssertionIssuerTests.cs` (new) |
| C | Admin app integration test (full flow) | 1, 2 | ~100 | `tests/Rag.IntegrationTests/AdminAppIntegrationTests.cs` (new) |

Units A and B are independent and can be implemented in parallel. Unit C depends on Unit B.

### Risks

- **Cloudflare JWKS dependency**: The admin app needs to validate Cloudflare Access JWTs, which requires fetching the team domain's JWKS endpoint. For testing, we need a mock JWKS endpoint or a test-mode validator that accepts a configured test key. Mitigation: use a configurable validator that accepts either a JWKS endpoint (production) or a static public key (test).
- **Admin app deployment surface**: Introducing a new project adds deployment complexity. Mitigation: the app is minimal (3 files) and can be deployed as a separate container in the same Dokploy environment.
- **Migration compatibility test timing**: The test must ensure the migration is applied before creating pre-admin data. Mitigation: use the existing `MigrateAsync()` pattern from `AdminApiTests.InitializeAsync`.
- **Expiry test clock manipulation**: The test backdates `ExpiresAt` via SQL rather than manipulating the system clock. Mitigation: this is a standard integration test pattern and avoids flaky time-based tests.

### Ready for Proposal

**Yes**. The orchestrator should tell the user:

1. Three scenarios (migration compatibility ×2, expiry rejection ×1) are test-only and can be addressed with ~150–200 lines of focused integration tests. No production code changes.
2. Two scenarios (Cloudflare assertion issuance) require building the minimal admin app that the design and deployment docs already reference. Estimated ~250–350 lines for the app + unit tests, plus ~100 lines for an integration test.
3. Total estimated effort: ~500–650 lines across 3 work units (A: test-only, B: admin app, C: admin app integration). Units A and B are independent; C depends on B.
4. The admin app is a new project (`src/Rag.AdminApp/`) that validates Cloudflare Access JWTs and issues RS256-signed assertions. It uses a configurable validator (JWKS endpoint for production, static key for test).
5. All existing implementation is preserved. No changes to production code for scenarios 3–5; new project for scenarios 1–2.
