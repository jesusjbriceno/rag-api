# Exploration: AdminApp Deployable Dashboard (Corrected)

## Purpose

Map the deployment gap for `Rag.AdminApp` — what exists, what is missing, and what remains unknown — for an admin app that authenticates operators via Cloudflare Access and emits assertions/machine proofs to the existing admin API. This exploration states only verifiable facts with source evidence and explicitly identifies unknowns. It does not implement anything.

## Commit Base

`a5ad805` on `develop`. No deploy, push, tag, or publication was performed. RDD clone-scope is off and untouched.

## Correction Notice

The previous exploration incorrectly concluded "Dokploy references are stale. The project has migrated to Coolify" without verifying primary Dokploy or Cloudflare Access documentation. It also selected a Coolify-only exposure/topology model without verifying the actual deployment platform. This corrected version:

1. States only what is verifiable from repository evidence, with source paths and line references.
2. Explicitly marks what remains unknown and requires external verification.
3. Does not invent topology, service exposure, secret names, host names, or Cloudflare policy configuration.
4. Inspects `admin-application` artifacts to determine whether a BFF host is planned.
5. Distinguishes hosting/deployment packaging from application implementation.

## Current State (verified from source)

### Rag.AdminApp class library

| Concept | Path | Status | Evidence |
| --------- | ------ | -------- | ---------- |
| Class library | `src/Rag.AdminApp/Rag.AdminApp.csproj` | ✅ net10.0, `System.IdentityModel.Tokens.Jwt` | Source file exists |
| `CloudflareAccessValidator` | `src/Rag.AdminApp/CloudflareAccessValidator.cs` | ✅ RS256 JWT validation, returns opaque subject or null | Source: accepts `CloudflareAccessOptions(Issuer, Audience, Keys)`, validates with `JwtSecurityTokenHandler`, 30s clock skew, RS256 only |
| `AdminAssertionIssuer` | `src/Rag.AdminApp/AdminAssertionIssuer.cs` | ✅ 60-second RS256 assertion with sub, app_id, jti, iss, aud, exp | Source file exists |
| Unit tests | `tests/Rag.UnitTests/AdminAppTests.cs` | ✅ 4 tests (valid/invalid credential, assertion claims, API cross-validation) | Source file exists |
| Executable host | `src/Rag.AdminApp.Host/` | ❌ Does not exist | `glob src/Rag.AdminApp.Host/**` returns no results; `grep Rag.AdminApp.Host src/` returns no matches |
| `Program.cs` | — | ❌ No HTTP pipeline | No host project exists |
| `AdminIdentityHeaderPolicy` | `src/Rag.Infrastructure/AdminIdentityHeaderPolicy.cs` | ✅ Allows `Cf-Access-Jwt-Assertion`, `Cf-Access-Authenticated-User-Email`, `Cf-Access-Authenticated-User-Groups` headers | Source: lines 8-10 |

The class library is a dependency — it cannot run standalone. It needs an ASP.NET Core host to expose HTTP endpoints.

### Dockerfile

| Target | Status | Evidence |
| -------- | -------- | ---------- |
| `build` | ✅ Multi-stage SDK restore/build | `Dockerfile` line 14: `FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build` |
| `runtime` | ✅ aspnet:10.0, non-root `rag` user (uid 10001) | `Dockerfile` line 26: `FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime` |
| `api` | ✅ Publishes `Rag.Api`, port 8080 | `Dockerfile` line 36: `FROM runtime AS api` |
| `operator` | ✅ Publishes `Rag.Operator` | `Dockerfile` line 43: `FROM runtime AS operator` |
| `admin` | ❌ Does not exist | No `FROM runtime AS admin` stage in Dockerfile |

### Health endpoints (API reference)

| Route | Purpose | Evidence |
| ------- | --------- | ---------- |
| `GET /api/v1/health/live` | Process liveness (anonymous) | Referenced in `docs/deployment/coolify.md` startup table |
| `GET /api/v1/health/ready` | PostgreSQL + llama.cpp dependency check (anonymous) | Referenced in `docs/deployment/coolify.md` startup table |
| `GET /api/v1/health` | Version info (anonymous) | Referenced in `docs/deployment/coolify.md` startup table |

### CI/CD pipelines

| Workflow | Trigger | Components published | Evidence |
| ---------- | --------- | --------------------- | ---------- |
| `ci-pr.yml` | PR to `develop` | Build + test gate, Compose validation. No publish. | Workflow file exists |
| `ci-develop.yml` | Push to `develop` | `api` + `operator` (amd64 + arm64), SonarQube, Trivy, cosign, SBOM, SLSA | Workflow file exists |
| `ci-release.yml` | Semver tag `v*` | Same as develop + companion (win-x64) + GitHub Release | Workflow file exists |

**Images published**: Only `ghcr.io/jesusjbriceno/rag-api` and `ghcr.io/jesusjbriceno/rag-operator`. No admin image.

### Compose files

| File | Services | Purpose | Evidence |
|------|----------|---------|----------|
| `compose.coolify.yaml` | postgres, model-download, llama-cpp, migrate, api | Production Coolify stack (pull-only, no ports, no domains) | Source: 5 services defined |
| `compose.dev.yaml` | api, migrate (build overrides) | Local dev loopback | Source file exists |

**No admin service** in either file.

### Compose validator constraints

The validator at `scripts/validate-coolify-compose.py` enforces:

- Exactly `{postgres, model-download, llama-cpp, migrate, api}` — no more, no less (line: `required_services = {"postgres", "model-download", "llama-cpp", "migrate", "api"}`)
- No `build` sections in production (pull-only)
- Only `ghcr.io/jesusjbriceno/rag-api` and `ghcr.io/jesusjbriceno/rag-operator` repositories (lines: `APP_IMAGES` dict)
- `pull_policy: always`
- Immutable tag or digest reference (no `latest`)
- No published ports
- Only implicit default network

Adding an admin service requires updating `required_services` and `APP_IMAGES`.

### Deployment documentation — Dokploy references (verified)

| Location | Line | Content | Classification |
| ---------- | ------ | --------- | ---------------- |
| `docs/deployment/coolify.md` | 198 | "**Dokploy ARM64 happy path:** verify each ordinary release..." | Stale reference in Coolify deployment doc |
| `README.md` | 80 | "...Docker selects `linux/arm64` automatically on an ARM64 Dokploy host." | Stale reference in README |
| `openspec/changes/client-administration/specs/admin-assertion-auth/spec.md` | 11 | "...in the same private Dokploy environment." | Archived spec (client-administration is completed/archived) |
| `openspec/changes/client-administration/exploration-scope-completion.md` | 98 | "...in the same Dokploy environment." | Archived exploration |

**What is verifiable from the repository:**

- The deployment documentation is titled "Deploy the private RAG stack with Coolify" (`docs/deployment/coolify.md` line 1)
- The Compose file is named `compose.coolify.yaml`
- The Compose validator is named `validate-coolify-compose.py` and enforces Coolify-specific constraints
- The `admin-application` proposal explicitly lists "Replace 'Dokploy' → 'Coolify' in deployment docs" as in-scope work
- The `admin-deployment` spec states: "Deployment documentation MUST use Coolify and MUST NOT retain Dokploy references"
- No Dokploy configuration files exist in the repository (`glob **/dokploy*` returns no results; no YAML/JSON/TOML files reference Dokploy)

**What is NOT verifiable from the repository:**

- Whether Dokploy is actually deployed or in use on external infrastructure
- Whether Coolify is the actual production deployment platform
- Whether the Dokploy references are truly "stale" or indicate a parallel/alternative deployment target
- Whether the user intends to use Dokploy, Coolify, or another platform for the admin app

### admin-application change artifacts (planned scope)

The `admin-application` change has complete proposal/spec/design artifacts:

| Artifact | Status | Key content |
| ---------- | -------- | ------------- |
| `openspec/changes/admin-application/proposal.md` | ✅ Complete | Defines BFF + React SPA, single Coolify container, Dockerfile target, Compose service, CI pipeline, Dokploy→Coolify doc correction |
| `openspec/changes/admin-application/exploration.md` | ✅ Complete | Detailed approach analysis; recommends ASP.NET Core BFF + React SPA |
| `openspec/changes/admin-application/design.md` | ✅ Complete | Architecture: single ASP.NET Core BFF (`src/Rag.AdminApp.Host/`) serving React SPA, Cloudflare JWT validation, assertion issuance, HMAC signing, API proxy, static file serving |
| `openspec/changes/admin-application/specs/admin-bff-auth/spec.md` | ✅ Complete | BFF validates Cloudflare Access, allowlists identities, issues assertions, proxies to API |
| `openspec/changes/admin-application/specs/admin-deployment/spec.md` | ✅ Complete | "Private Coolify deployment", Dockerfile target, Compose service, CI coverage, Coolify docs without Dokploy references |

**The BFF host (`src/Rag.AdminApp.Host/`) does NOT exist yet.** It is planned in the `admin-application` change but not implemented. The design specifies:

- Single ASP.NET Core host serving BFF + SPA together
- Cloudflare JWT validation per browser request (stateless)
- Typed proxy for exactly 9 admin API operations
- SPA served as static files from the same container

**The `admin-deployment` spec** explicitly requires:

- "The project MUST provide an admin container target, Coolify Compose service, and CI coverage"
- "The service MUST NOT be publicly exposed outside its Cloudflare-protected entry point"
- "Deployment documentation MUST use Coolify and MUST NOT retain Dokploy references"

## Deployment Platform — Unknowns

### What this exploration cannot determine

| Unknown | Why it matters | What would resolve it |
| --------- | --------------- | ---------------------- |
| Actual deployment platform (Dokploy vs Coolify vs other) | Determines Compose structure, service exposure model, domain configuration | User confirmation of target platform |
| Cloudflare Access policy existence | Determines whether the admin app can be protected by Cloudflare Access | Verification in Cloudflare Zero Trust dashboard |
| Cloudflare team domain | Required for JWKS endpoint URL, JWT issuer validation | Cloudflare Zero Trust dashboard settings |
| Cloudflare Access application configuration | Determines how JWT assertions are forwarded to the origin | Cloudflare Zero Trust application settings |
| Target host/domain for admin app | Required for Cloudflare Access application, DNS, TLS | Infrastructure decision |
| Network topology (same stack vs separate stack) | Determines how admin app reaches the API | Architecture decision based on platform |

### What the repository evidence indicates

The repository's deployment configuration, documentation, and planned `admin-application` change all target **Coolify** as the deployment platform:

1. `compose.coolify.yaml` is the production Compose file
2. `docs/deployment/coolify.md` is the deployment guide
3. The Compose validator enforces Coolify-specific constraints
4. The `admin-application` proposal, design, and specs reference Coolify exclusively
5. The `admin-deployment` spec mandates Coolify terminology and prohibits Dokploy references
6. No Dokploy configuration exists in the repository

**However**, this exploration cannot verify whether Coolify is the actual production platform without external infrastructure access. The Dokploy references in `docs/deployment/coolify.md` (line 198) and `README.md` (line 80) may indicate:

- Stale documentation that was not updated after a migration to Coolify (consistent with the `admin-application` proposal's plan to fix them)
- A parallel or alternative deployment target that is not yet configured in the repository
- An error in the documentation

**This exploration does not select an exposure/topology model.** That decision requires confirming the deployment platform and Cloudflare Access configuration.

## Cloudflare Access Integration — Verified Facts

### What is implemented (verifiable from source)

| Component | Location | Behavior | Evidence |
| ----------- | ---------- | ---------- | ---------- |
| `CloudflareAccessValidator` | `src/Rag.AdminApp/CloudflareAccessValidator.cs` | Validates RS256 JWTs with configurable issuer, audience, and keys. Returns opaque subject or null. 30s clock skew. | Source code |
| `CloudflareAccessOptions` | `src/Rag.AdminApp/CloudflareAccessValidator.cs` | Record: `Issuer`, `Audience`, `Keys` (list of `CloudflareAccessKey(KeyId, PublicKeyPem)`) | Source code |
| `CloudflareAccessKey` | `src/Rag.AdminApp/CloudflareAccessValidator.cs` | Record: `KeyId`, `PublicKeyPem` | Source code |
| `AdminIdentityHeaderPolicy` | `src/Rag.Infrastructure/AdminIdentityHeaderPolicy.cs` | Allows `Cf-Access-Jwt-Assertion`, `Cf-Access-Authenticated-User-Email`, `Cf-Access-Authenticated-User-Groups` CORS headers | Source code, lines 8-10 |
| Unit tests | `tests/Rag.UnitTests/AdminAppTests.cs` | 4 tests: valid credential → subject, invalid credential → null, assertion claims, API cross-validation | Source file exists |

### What Cloudflare Access requires (standard protocol, not repo-specific)

Based on the implemented `CloudflareAccessValidator` interface and standard Cloudflare Access behavior:

1. **Cloudflare Access authenticates users** at the edge and injects `Cf-Access-Jwt-Assertion` header on forwarded requests to the origin
2. **The origin (BFF) validates the JWT** using the team domain's JWKS endpoint or configured public keys
3. **The validator requires**: issuer (team domain URL), audience (application AUD tag), and one or more public keys (from JWKS or static PEM)

### What is NOT verified

| Unknown | Why it matters |
| --------- | --------------- |
| Whether a Cloudflare Access policy exists for any admin domain | No admin domain or application is configured in the repository |
| Cloudflare team domain URL | Required for `CloudflareAccessOptions.Issuer` |
| Cloudflare application AUD tag | Required for `CloudflareAccessOptions.Audience` |
| JWKS endpoint URL or public keys | Required for `CloudflareAccessOptions.Keys` |
| How Cloudflare Access forwards the JWT to the origin | Standard behavior sends `Cf-Access-Jwt-Assertion` header, but specific application configuration is unknown |
| Whether the admin app will be behind Cloudflare Access directly or through a reverse proxy | Affects exposure model |

## Hosting/Deployment vs Application Implementation — Distinction

The `admin-application` change covers both:

### Application implementation (not part of this change)

- BFF host (`src/Rag.AdminApp.Host/`) — ASP.NET Core HTTP pipeline
- React SPA (`apps/admin-ui/`) — frontend application
- Cloudflare JWT validation middleware
- Assertion issuance HTTP endpoint
- HMAC signing and API proxy logic
- Static file serving for SPA

### Hosting/deployment packaging (this change's scope)

- Dockerfile `admin` target (builds the admin host into a container image)
- CI pipeline coverage (multiarch build, scan, sign, attest for admin image)
- Compose service definition (admin service in `compose.coolify.yaml`)
- Compose validator update (accept admin service and image repository)
- Documentation correction (Dokploy→Coolify references)
- Admin-specific secret documentation

**Critical dependency**: The Dockerfile `admin` target requires the BFF host project (`src/Rag.AdminApp.Host/`) to exist. Without it, there is nothing to `dotnet publish`. This creates a sequencing constraint:

| Option | Description | Tradeoff |
| -------- | ------------- | ---------- |
| A: Deployment packaging includes minimal BFF scaffold | This change creates a minimal `Program.cs` that references `Rag.AdminApp` and exposes a health endpoint | Enables independent deployment packaging; scaffold may need rework when full BFF is implemented |
| B: Deployment packaging is sequenced after BFF implementation | This change waits for `admin-application` to deliver the BFF host | Clean separation; delays deployment packaging until BFF is ready |
| C: Dockerfile target is defined but uses class library with minimal wrapper | Define the Dockerfile stage that publishes a thin host wrapper around the class library | Intermediate; still requires some host code |

**The `admin-application` design already specifies** that deployment packaging is part of its scope (capability `admin-deployment`). If `adminapp-deployable-dashboard` is a separate change, it must either:

1. Be sequenced after `admin-application` delivers the BFF host
2. Include a minimal BFF scaffold sufficient for Docker build
3. Coordinate with `admin-application` to avoid duplicating deployment work

## What This Change Should Deliver

Based on verified repository evidence and the `admin-application` change's planned scope:

### In scope (deployment packaging only)

1. Dockerfile `admin` target (requires BFF host to exist or minimal scaffold)
2. CI pipeline coverage for admin app in `ci-develop.yml` and `ci-release.yml`
3. GHCR image repository for admin app (name TBD — decision needed)
4. Compose service in `compose.coolify.yaml` (pull-only, no ports — topology TBD)
5. Compose validator update (`required_services`, `APP_IMAGES`)
6. Documentation correction: Dokploy→Coolify in `docs/deployment/coolify.md` and `README.md`
7. Admin-specific secret documentation

### Out of scope

- BFF implementation (`Rag.AdminApp.Host`) — planned in `admin-application` change
- React SPA (`apps/admin-ui/`) — planned in `admin-application` change
- New API endpoints
- Cloudflare Access policy configuration (external to this repo)
- E2E browser tests
- Historical import companion
- Deployment platform selection (Dokploy vs Coolify vs other) — requires user confirmation
- Exposure/topology model selection — requires confirming deployment platform and Cloudflare Access configuration

### Decisions That Cannot Be Invented

| Decision | Why it matters | Options |
| ---------- | --------------- | --------- |
| Deployment platform | Determines Compose structure, service exposure, domain configuration | Dokploy, Coolify, other — requires user confirmation |
| Admin image repository name | GHCR publication, Compose reference, cosign identity | `rag-admin`, `rag-admin-app`, `rag-adminapp` |
| Admin app deployment topology | Compose structure, network isolation | Same Coolify stack as API, separate stack connected via predefined network, other |
| Admin app port | Health check, internal routing | 8080 (same as API), or different |
| Sequencing with BFF host | Whether this change includes a minimal scaffold | Option A/B/C above |
| Admin health endpoint path | Healthcheck, monitoring | `/health/live` and `/health/ready`, or `/api/v1/health/*` to match API pattern |
| Admin-specific secrets naming convention | Environment variable names | Follow `Admin__*` pattern or match API's `AdminAssertion:*`/`AdminAppAuth:*` |
| Cloudflare Access team domain | JWT issuer validation | Configurable via environment; not invented here |
| Cloudflare Access application AUD | JWT audience validation | Configurable via environment; not invented here |

## Relationship to admin-application Change

The `admin-application` change already includes `admin-deployment` as a capability with:

- Dockerfile target
- Compose service
- CI pipeline
- Dokploy→Coolify doc correction

**This creates overlap.** The `adminapp-deployable-dashboard` change is the **deployment-packaging slice** of the larger `admin-application` effort. It can be:

1. **A separate SDD change** — if deployment packaging should proceed independently (e.g., to unblock infrastructure setup before the BFF is complete)
2. **Part of the `admin-application` change** — if deployment packaging is done together with BFF implementation (as the `admin-application` proposal already plans)

If this proceeds as a separate change, it must coordinate with `admin-application` to avoid:

- Duplicating the Dockerfile target work
- Duplicating the Compose service work
- Duplicating the CI pipeline work
- Duplicating the documentation correction work

## Scope Limits

1. **No BFF logic**: This change packages; it does not implement the proxy, Cloudflare middleware, or assertion issuance HTTP pipeline.
2. **No frontend**: No React, no Vite, no Node.js toolchain.
3. **No new API endpoints**: The admin API is complete; this change only deploys its consumer.
4. **No Cloudflare Access policy**: External configuration, not code.
5. **No E2E tests**: Browser testing is deferred.
6. **No companion integration**: The historical import companion is a separate change.
7. **No deployment platform selection**: This exploration identifies the evidence but does not select Dokploy, Coolify, or another platform.
8. **No exposure/topology model selection**: Requires confirming deployment platform and Cloudflare Access configuration.

## Risks

| Risk | Likelihood | Impact | Mitigation |
| ------ | ----------- | -------- | ------------ |
| BFF host doesn't exist yet | Certain (verified) | Blocks Dockerfile admin target | Decide sequencing (Option A/B/C) before starting |
| Compose validator rejects new service | Certain | Blocks Compose work | Update validator in same change |
| Overlap with admin-application change | High | Duplicate work or conflicts | Coordinate scope boundaries |
| Deployment platform uncertainty | Unknown | May invalidate topology assumptions | Confirm platform before implementing Compose service |
| Admin image repo not pre-created in GHCR | Medium | Blocks CI | Create repo before first push, or let first push auto-create |
| Admin secrets conflict with API secrets | Low | Medium | Use distinct prefix |

## Recommendation

1. **Confirm the deployment platform** (Dokploy, Coolify, or other) before selecting an exposure/topology model. The repository evidence indicates Coolify, but external infrastructure verification is needed.

2. **Confirm Cloudflare Access configuration** exists or is planned. The `CloudflareAccessValidator` is implemented, but no Cloudflare Access policy, team domain, or application AUD is configured in the repository.

3. **Decide whether this is a separate change or part of admin-application.** The `admin-application` change already plans deployment packaging. If this proceeds separately, coordinate to avoid duplication.

4. **Decide the sequencing model** (Option A/B/C) for the BFF host dependency before starting implementation.

5. **Do not invent topology, service exposure, secret names, host names, or Cloudflare policy configuration.** These require external verification and user decisions.

## Evidence Required

| Evidence | Source | Purpose |
| ---------- | -------- | --------- |
| Dockerfile `admin` target builds | `docker build --target admin` | Packaging works |
| CI pipeline includes admin matrix entry | Workflow YAML inspection | Publication coverage |
| Compose validator passes with admin service | `scripts/validate-coolify-compose.sh` | Topology correctness |
| No "Dokploy" references in deployment docs | `grep -r Dokploy docs/ README.md` | Documentation currency |
| Admin image follows same guarantees as API/operator | CI workflow inspection | Security parity |
| Admin service has no published ports | Compose inspection | No unintended public exposure |
| Deployment platform confirmed | User confirmation | Topology model selection |
| Cloudflare Access policy exists | Cloudflare dashboard verification | Integration feasibility |
