# Proposal: AdminApp Deployable Dashboard (Revised)

## Intent

Deliver an operational AdminApp BFF host and integrate it into the existing Coolify production deployment. The BFF validates Cloudflare Access credentials, issues RS256 assertions, generates HMAC machine proofs, and proxies administrative requests to the existing API. The admin service is added to `compose.coolify.yaml` as the sole publicly-reachable entry point, protected by Cloudflare Access. The API, PostgreSQL, and llama.cpp remain internal to the Docker network with no public exposure.

This change produces a deployable BFF image with the same publication guarantees as `rag-api` and `rag-operator` (multiarch build, Trivy scan, cosign signing, SBOM, SLSA provenance). No deploy, tag, manual publication, commit, or push is performed. RDD remains disabled.

### Revision: Dokploy → Coolify

The original proposal specified a private Dokploy deployment manifest (`compose.dokploy.yaml`) as an additional, independent deployment target. **This revision replaces Dokploy with Coolify.** The user has made an explicit decision: production runs on Coolify on a single server. Dokploy is out of scope.

The BFF host shell (`src/Rag.AdminApp.Host/`) is already implemented (Unit 1 complete). It is preserved as-is; no code changes to the host are part of this proposal revision.

## Scope

### In Scope

1. **BFF host** (`src/Rag.AdminApp.Host/`) — **already implemented (Unit 1)**
   - ASP.NET Core minimal host referencing `Rag.AdminApp` and `Rag.Infrastructure`
   - Cloudflare Access JWT validation per request (reuses `CloudflareAccessValidator`)
   - Administrator identity allowlist (configurable, fail-closed)
   - Assertion issuance via `AdminAssertionIssuer` (60-second RS256 JWT)
   - Machine proof generation compatible with `AdminMachineProofVerifier` (HMAC-SHA256 canonical signing)
   - Typed admin proxy for the nine existing `/api/v1/admin/*` operations
   - Health endpoints: `GET /health/live` (liveness), `GET /health/ready` (API reachability)
   - Configuration via `IConfiguration` / environment variables — no hardcoded values
   - Forbidden identity header stripping (reuses `AdminIdentityHeaderPolicy`)
   - No SPA serving (deferred to `admin-application`)
   - **Status**: Unit 1 complete (shared wire contract + validated host shell). Units 2–5 remain.

2. **Dockerfile `admin` target**
   - Multi-stage: `build` stage publishes `Rag.AdminApp.Host`; `runtime`-derived `admin` stage
   - Non-root `rag` user (uid 10001), same base layers as `api` and `operator`
   - Port 8080, `DOTNET_EnableDiagnostics=0`
   - OCI labels: `org.opencontainers.image.version`, `org.opencontainers.image.revision`

3. **CI pipeline coverage** (`ci-develop.yml`, `ci-release.yml`)
   - Matrix entries for `admin` component (amd64 + arm64)
   - Same publication guarantees: Trivy HIGH/CRITICAL scan, cosign keyless signing, SPDX SBOM attestation, SLSA v1 provenance attestation, architecture tag collision check, multi-platform index promotion, publication proof artifact, async verification
   - GHCR image repository: `ghcr.io/jesusjbriceno/rag-adminapp` (name follows existing `rag-api` / `rag-operator` convention)

4. **Coolify Compose integration** (replaces Dokploy manifest)
   - Add an `admin` service to the existing `compose.coolify.yaml`
   - Admin service references the GHCR admin image with immutable tag/digest
   - `pull_policy: always`
   - Internal connectivity to the `api` service via the existing implicit default Docker network
   - Environment variables for all configuration — no secret values in the manifest
   - Healthcheck using `GET /health/ready`
   - No published ports in the Compose file (Coolify manages external exposure)
   - FQDN/domain configuration expressed as environment variable references (e.g. `${ADMINAPP_FQDN:?required}`) — no concrete hostname invented
   - Update `scripts/validate-coolify-compose.py` to validate the admin service: fixed image repository, immutable reference, pull policy, no published ports, required configuration references, no literal secret values, readiness healthcheck, internal-only network topology
   - Update `scripts/validate-coolify-compose.sh` to include the admin service in the required-services set
   - Add validator test fixtures covering admin service acceptance and rejection cases

5. **Cloudflare Access integration procedure** (documentation)
   - Document the Coolify resource configuration for AdminApp: FQDN/domain reference, Cloudflare Access application binding, and the requirement that AdminApp is the only publicly-reachable service
   - Document the environment variable references needed for Cloudflare Access JWT validation (issuer, audience, key ID, public key PEM)
   - Document the procedural steps for configuring Cloudflare Access to protect the AdminApp FQDN — without inventing team domain, AUD values, JWKS keys, or policy decisions
   - Explicitly state that API, PostgreSQL, and llama.cpp are never exposed publicly and remain on the internal Docker network only

6. **Configuration and secrets surface** (structure only, no values)
   - `CloudflareAccess__Issuer` — team domain URL (environment reference)
   - `CloudflareAccess__Audience` — application AUD tag (environment reference)
   - `CloudflareAccess__Keys__0__KeyId`, `CloudflareAccess__Keys__0__PublicKeyPem` — JWKS-derived validation keys
   - `AdminAssertion__Issuer`, `AdminAssertion__Audience`, `AdminAssertion__AppId`, `AdminAssertion__KeyId`, `AdminAssertion__PrivateKeyPem`
   - `AdminAppAuth__Apps__0__AppId`, `AdminAppAuth__Apps__0__KeyId`, `AdminAppAuth__Apps__0__CurrentSecret`, `AdminAppAuth__Apps__0__PreviousSecret`
   - `AdminApi__BaseUrl` — internal API endpoint (Docker network service name, e.g. `http://api:8080`)
   - `AllowedAdminSubjects__0` — allowlisted Cloudflare identity subjects
   - `ADMINAPP_FQDN` — the domain Coolify assigns to the admin service (reference only; no concrete value)
   - All values provided via environment variables; no defaults, no hardcoded secrets

7. **Focused tests**
   - Unit tests: Cloudflare validation (valid/invalid/expired), assertion issuance claims, machine proof canonicalisation and HMAC signing, allowlist enforcement, forbidden header rejection
   - Integration tests: `WebApplicationFactory`-based proxy round-trip for each of the nine admin operations, health endpoint behaviour, missing-configuration fail-closed
   - Build verification: `docker build --target admin` succeeds
   - Validator tests: updated Coolify Compose validator fixtures for the admin service

8. **Runbook** (documentation)
   - Health check procedure: `curl` commands for `/health/live` and `/health/ready`
   - Rollback procedure: revert to previous image tag, no data migration needed
   - First admin onboarding: configure Cloudflare Access policy, set allowed subjects, verify JWT validation, create first client via the API
   - Coolify configuration procedure: assign FQDN, bind Cloudflare Access application, inject environment variables

### Out of Scope

- React SPA / frontend (`admin-application` scope)
- SPA static file serving in the BFF (`admin-application` scope)
- Historical import UI or companion protocol (`admin-application` scope)
- **Dokploy deployment manifest** — Dokploy is out of scope; production uses Coolify exclusively
- Cloudflare Access policy creation or configuration (external infrastructure — this change documents the procedure but does not perform it)
- New API endpoints
- E2E browser tests
- Actual deployment, tag creation, manual publication, commit, or push
- RDD activation
- **Tailscale** — host administration tooling; out of scope for this change
- Modifying PostgreSQL, llama.cpp, or API service definitions in `compose.coolify.yaml` beyond adding the admin service
- Inventing concrete FQDN/hostnames, Cloudflare team domain, AUD values, JWKS keys, or any secret values

## Relationship to `admin-application`

| Aspect | This change (`adminapp-deployable-dashboard`) | `admin-application` |
| -------- | ----------------------------------------------- | --------------------- |
| BFF host (`src/Rag.AdminApp.Host/`) | **Creates** the operational host with auth, assertion, proof, proxy, health (Unit 1 done) | Consumes the host; adds SPA static file serving |
| Dockerfile `admin` target | **Creates** the target | Uses the target (no duplication) |
| CI pipeline for admin image | **Creates** matrix entries and publication guarantees | Uses the image (no duplication) |
| Coolify Compose service | **Creates** the admin service in `compose.coolify.yaml` and updates its validator | Does not touch Coolify Compose further |
| Coolify/Cloudflare Access procedure | **Documents** the FQDN/domain reference and Cloudflare Access binding procedure | May extend with SPA-specific Cloudflare Access routes |
| React SPA | Does NOT include frontend | **Creates** the SPA |
| Historical import UI | Does NOT include | **Creates** the UI |
| Dokploy | **Does NOT create** any Dokploy manifest | Does not touch Dokploy |

**Dependency direction:** `admin-application` depends on this change for the BFF host, Dockerfile target, CI image, and Coolify Compose integration. This change has no dependency on `admin-application`.

**Coordination rule:** The BFF host created here must be extensible — `admin-application` will add SPA static file serving and historical import endpoints to the same host. The host's `Program.cs` uses a composition pattern (service registration + middleware pipeline) that allows additive extension without rewriting. The Coolify Compose admin service must also be extensible for future SPA-related environment variables or labels that `admin-application` may add.

**Adjustment from original proposal:** The original proposal deferred the Coolify Compose service to `admin-application`. This revision brings it into this change because the user has decided that Coolify is the sole production target and the admin service must be deployed alongside the existing API/PostgreSQL/llama-cpp stack. `admin-application` no longer needs to create the Coolify Compose admin service; it inherits it from this change.

## Acceptance Criteria

- [ ] `docker build --target admin .` succeeds and produces a runnable image
- [ ] Image runs as non-root `rag` user (uid 10001)
- [ ] `GET /health/live` returns 200 when the process is running
- [ ] `GET /health/ready` returns 200 when the API is reachable, 503 otherwise
- [ ] Missing Cloudflare configuration causes startup failure (fail-closed)
- [ ] Missing or invalid Cloudflare JWT returns 401 without issuing an assertion
- [ ] Non-allowlisted subject returns 403 without issuing an assertion
- [ ] Valid allowlisted request receives a signed assertion and machine proof
- [ ] Proxy forwards exactly the nine admin operations with correct headers
- [ ] Forbidden identity headers are stripped from proxied requests
- [ ] CI pipeline builds admin image for amd64 + arm64 with scan, sign, attest, and proof
- [ ] GHCR image `ghcr.io/jesusjbriceno/rag-adminapp` is publishable with the same guarantees as `rag-api` and `rag-operator`
- [ ] `compose.coolify.yaml` contains an `admin` service referencing the GHCR image with immutable tag/digest
- [ ] `compose.coolify.yaml` admin service declares no published ports (Coolify manages external exposure)
- [ ] `compose.coolify.yaml` admin service contains no secret values — only environment variable references
- [ ] Admin service reaches the API service via the internal Docker network (same Compose project)
- [ ] All configuration is injectable via environment variables
- [ ] `scripts/validate-coolify-compose.py` validates the admin service (image repository, immutable reference, pull policy, no ports, no secrets, healthcheck, network topology)
- [ ] Unit, integration, and validator tests pass
- [ ] Runbook documents health check, rollback, first admin onboarding, and Coolify/Cloudflare Access configuration procedure
- [ ] No concrete FQDN, hostname, Cloudflare team domain, AUD, JWKS keys, or secret values are invented in any artifact

## Security Boundaries

### What this change enforces

- Cloudflare JWT validation is cryptographic (RS256, configurable issuer/audience/keys, 30s clock skew)
- Identity allowlist is fail-closed: no configuration = no access
- Assertion private keys and HMAC secrets never leave the BFF process
- Forbidden identity headers are stripped before proxying
- Machine proofs use HMAC-SHA256 with constant-time comparison
- Non-root container user
- No published ports in `compose.coolify.yaml` — AdminApp is the only service exposed through Coolify's external routing, and only behind Cloudflare Access
- API, PostgreSQL, and llama.cpp remain on the internal Docker network with no public exposure

### What this change does NOT control

- Cloudflare Access policy existence and configuration (external)
- Cloudflare team domain, application AUD, or JWKS key rotation (external)
- Coolify FQDN assignment and TLS termination (external infrastructure)
- Network-level isolation beyond the Compose default network (host/Docker configuration)
- Administrator identity selection (operational decision)
- Secret injection mechanism (Coolify environment variables — operational)
- Tailscale configuration and access (host administration, out of scope)

## Open Operational Decisions

| Decision | Why it is open | Who decides |
| ---------- | ---------------- | ------------- |
| AdminApp FQDN/domain assigned in Coolify | Coolify resource configuration | Operator |
| Cloudflare Access team domain URL | External service configuration | Operator |
| Cloudflare Access application AUD tag | External service configuration | Operator |
| Cloudflare Access JWKS key IDs and public keys | External service configuration | Operator |
| Allowed administrator subjects | Business/operational policy | Operator |
| Assertion issuer/audience values | Domain policy | Operator |
| Admin app ID and HMAC key provisioning | Secret lifecycle | Operator |
| API internal base URL (Docker service name) | Compose service discovery (likely `http://api:8080`) | Operator |
| GHCR repository creation (auto on first push vs pre-created) | GitHub organisation policy | Operator |
| Cloudflare Access application policy rules (who can access) | Business/operational policy | Operator |

None of these decisions require values to be invented in this proposal. The BFF host accepts all of them as configuration; `compose.coolify.yaml` references them as environment variables.

## Risks

| Risk | Likelihood | Impact | Mitigation |
| ------ | ----------- | -------- | ------------ |
| BFF host requires rework when SPA serving is added | Low | Medium | Composition pattern in `Program.cs`; additive extension point |
| GHCR `rag-adminapp` repository does not exist | Medium | Low | Auto-created on first push, or pre-create before CI runs |
| Coolify validator change breaks existing API/migrate validation | Low | High | Additive change: extend the required-services set and add admin-specific checks; existing checks remain unchanged; validator tests cover both positive and negative cases |
| Admin image CI increases pipeline duration | Medium | Low | Matrix parallelism; same pattern as existing components |
| BFF proxy does not cover all nine admin operations | Low | High | Integration test per operation; explicit allowlist |
| Coolify FQDN or Cloudflare Access misconfiguration exposes admin publicly without Access protection | Low | High | Runbook documents the binding procedure; validator checks no published ports; Cloudflare Access is the only public entry point |

## Rollback

- Remove the `admin` service from `compose.coolify.yaml`
- Remove the admin validation checks from `scripts/validate-coolify-compose.py`
- Remove the `admin` Dockerfile target and CI matrix entries
- Remove the BFF host project
- No data migration needed; the API, PostgreSQL, and llama.cpp services are unchanged
- Existing `compose.coolify.yaml` services (postgres, model-download, llama-cpp, migrate, api) are not modified — only the admin service is added

## Success Criteria

- A `docker build --target admin` produces an image that starts, validates Cloudflare JWTs, issues assertions, signs machine proofs, and proxies to the API
- CI publishes the admin image with the same security guarantees as API and operator
- `compose.coolify.yaml` includes the admin service with no public port exposure, internal Docker connectivity to the API, and environment-variable-only configuration
- The Coolify Compose validator accepts the admin service and rejects invalid configurations
- AdminApp is the only publicly-reachable service, protected by Cloudflare Access; API, PostgreSQL, and llama.cpp remain internal
- An operator can follow the runbook to verify health, roll back, configure Coolify/Cloudflare Access, and onboard the first administrator
- The `admin-application` change can extend the BFF host with SPA serving without rewriting it

## Downstream Artifacts Requiring Regeneration

This proposal revision changes the deployment target from Dokploy to Coolify. The following downstream SDD artifacts were produced against the original Dokploy-oriented proposal and **must be regenerated** to reflect the Coolify integration:

| Artifact | Topic Key / Path | Status | Action Required |
| --- | --- | --- | --- |
| Spec | `specs/adminapp-deployment/spec.md` | Exists | Regenerate: replace "Private Dokploy deployment manifest" requirement with "Coolify Compose integration" requirement; update acceptance scenarios; add Cloudflare Access integration procedure requirement; remove Dokploy references |
| Design | `design.md` | Exists | Regenerate: replace Decision 11 (Dokploy manifest) with Coolify Compose integration decision; update file-level change plan (remove `compose.dokploy.yaml`, `validate-dokploy-compose.*`; add `compose.coolify.yaml` modifications, `validate-coolify-compose.py` updates); update verification strategy; update rollout |
| Tasks | `tasks.md` | Exists | Regenerate: replace Unit 4 (Dokploy package, validator, runbook) with Coolify Compose integration tasks; update validator test tasks; update runbook tasks to include Coolify/Cloudflare Access procedure; update design-inconsistency items (remove Dokploy-specific items) |
| Apply Progress | `apply-progress.md` | Exists | Preserve Unit 1 record (host shell is unchanged). Units 2–5 task references will need updating once tasks are regenerated |

**Unit 1 is unaffected**: the host shell implementation (shared wire contract + validated host shell) is independent of the deployment target and remains valid.

## Status

- Phase: proposal (revised)
- Next: spec (regeneration required)
- Artifact store: openspec
- Skill resolution: paths-injected
- Revision: Dokploy → Coolify (explicit user decision)
