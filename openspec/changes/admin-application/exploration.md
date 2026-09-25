# Exploration: Admin Application (Browser-Based Administration)

## Purpose

Explore the internal browser administration application that consumes the already-delivered `/api/v1/admin/*` API. First release serves one or two trusted administrators. This exploration identifies boundaries and interfaces with a future one-time historical ingestion workflow from local Dropbox-synchronized folders, without implementing or designing that workflow.

## Current State

### Confirmed from source

| Concept | Location | Status |
|---------|----------|--------|
| Admin API endpoints | `src/Rag.Api/AdminEndpoints.cs` | ✅ Implemented — 9 routes under `/api/v1/admin` |
| Admin authentication | `src/Rag.Api/AdminAuthentication.cs` | ✅ Implemented — dual machine proof (HMAC) + assertion (RS256) |
| Admin assertion issuer | `src/Rag.AdminApp/AdminAssertionIssuer.cs` | ✅ Class library — RS256 JWT, 60s lifetime |
| Cloudflare validator | `src/Rag.AdminApp/CloudflareAccessValidator.cs` | ✅ Class library — RS256 JWKS/static key |
| Admin app host | `src/Rag.AdminApp.Host/` | ❌ Does not exist |
| Frontend UI | `apps/admin-ui/` | ❌ Does not exist — no `apps/` directory |
| Frontend tooling | React, TypeScript, Vite | ❌ Not configured |
| Dockerfile admin target | `Dockerfile` | ❌ Only `api` and `operator` targets exist |
| Compose admin service | `compose.coolify.yaml` | ❌ No admin app service defined |
| CI frontend pipeline | `.github/workflows/` | ❌ .NET-only build/test |
| Test coverage | `tests/Rag.UnitTests/AdminAppTests.cs` | ✅ 4 unit tests for library classes |
| Deployment docs | `docs/deployment/client-administration.md` | ⚠️ References stale "Dokploy" terminology |

### What exists today

1. **Admin API** (`/api/v1/admin/*`): Full CRUD for clients, credentials, audit. Requires dual authentication (HMAC machine proof + RS256 assertion). Keyset pagination, idempotency, replay protection, version guards, secret-once delivery.

2. **Admin class library** (`src/Rag.AdminApp/`): Two classes — `CloudflareAccessValidator` (validates Cloudflare Access JWT, returns opaque subject) and `AdminAssertionIssuer` (issues short-lived RS256 assertion with `sub`, `app_id`, `jti`, `iss`, `aud`, `exp`). No executable host.

3. **Trust model**: Cloudflare Access authenticates humans → admin app validates Cloudflare JWT → app issues assertion → app HMAC-signs request → API verifies both. Browser never holds private key or HMAC secret.

4. **Deployment**: Coolify with Docker Compose. API and operator are containerized. Admin app deployment is "packaging deferred" per design.

5. **Testing**: 157 tests passing (99 unit + 58 Testcontainers integration). Admin app library has 4 unit tests. No E2E browser tests.

### What does NOT exist

- No ASP.NET Core host for the admin app (no `Program.cs`, no HTTP pipeline)
- No React/TypeScript frontend
- No BFF (Backend-For-Frontend) that bridges browser ↔ API
- No Dockerfile target for admin app
- No Compose service for admin app
- No CI pipeline for frontend build/test
- No Cloudflare Access integration test (only unit tests for validator/issuer)
- No documentation for admin app deployment (only API-side rollout guide)

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Rag.AdminApp.Host/` | New | ASP.NET Core BFF: Cloudflare validation, assertion issuance, HMAC signing, static file serving, API proxy |
| `apps/admin-ui/` | New | React/TypeScript SPA: client/credential/audit UI |
| `Dockerfile` | Modified | Add `admin` build target |
| `compose.coolify.yaml` | Modified | Add `admin` service (private, no public domain) |
| `compose.dev.yaml` | Modified | Add `admin` service for local dev |
| `.github/workflows/ci-*.yml` | Modified | Add Node.js setup, frontend build/test, admin app build |
| `docs/deployment/client-administration.md` | Modified | Replace stale "Dokploy" references with "Coolify" |
| `Rag.sln` | Modified | Add `Rag.AdminApp.Host` project |
| `Directory.Packages.props` | Modified | Add frontend-related packages if needed (e.g., `Microsoft.AspNetCore.SpaServices.Extensions`) |
| `tests/Rag.AdminApp.Host.Tests/` | New | Unit tests for BFF (Cloudflare validation, assertion issuance, HMAC signing, proxy logic) |
| `tests/Rag.AdminApp.E2ETests/` | New (optional) | Playwright or Cypress E2E tests for admin UI |

## Approaches

### Approach 1: ASP.NET Core BFF + React SPA (Recommended)

**Description**: Single ASP.NET Core host (`src/Rag.AdminApp.Host/`) that serves the React SPA as static files and acts as a BFF. The BFF validates Cloudflare Access JWT, issues assertions, HMAC-signs requests, and proxies to the API. React SPA is built with Vite and deployed as static assets.

**Architecture**:
```
Browser (React SPA)
  ↓ (cookie/session)
ASP.NET Core BFF (Rag.AdminApp.Host)
  ├── Validates Cloudflare Access JWT (from cookie/header)
  ├── Issues RS256 assertion
  ├── HMAC-signs request
  ├── Proxies to API
  └── Serves React SPA (static files)
  ↓ (HMAC + assertion)
API (/api/v1/admin/*)
```

**Pros**:
- Single container deployment (simpler Coolify config)
- BFF handles all secrets (private key, HMAC secret) — browser never sees them
- ASP.NET Core already has `CloudflareAccessValidator` and `AdminAssertionIssuer` — reuse directly
- Static file serving is built-in (`app.UseStaticFiles()`)
- Can use ASP.NET Core authentication middleware for Cloudflare JWT
- Easy to add server-side session management if needed later
- Matches the design's "minimal admin app" intent

**Cons**:
- Requires Node.js toolchain for React build (adds CI complexity)
- Two technology stacks (.NET + React) in one repository (already the case with .NET + operator CLI)
- BFF becomes a single point of failure (but it's stateless, so horizontal scaling is trivial)

**Effort**: Medium-High
- BFF: ~400-600 lines (Program.cs, Cloudflare auth middleware, assertion issuer, HMAC signer, API proxy controller, static file config)
- React SPA: ~800-1200 lines (client list/detail/create, credential list/issue/rotate/revoke, audit list, auth flow, API client)
- Dockerfile + Compose: ~50 lines
- CI: ~100 lines
- Tests: ~300-500 lines (BFF unit tests, React component tests, optional E2E)
- **Total**: ~1650-2450 lines (exceeds 400-line review budget → chained PRs required)

### Approach 2: Separate Frontend + BFF

**Description**: React SPA deployed separately (e.g., Cloudflare Pages, Vercel, or a separate container). BFF is API-only (no static file serving). Frontend calls BFF directly.

**Architecture**:
```
Browser (React SPA, deployed separately)
  ↓ (HTTPS)
ASP.NET Core BFF (API-only)
  ├── Validates Cloudflare Access JWT
  ├── Issues assertion
  ├── HMAC-signs request
  └── Proxies to API
  ↓ (HMAC + assertion)
API (/api/v1/admin/*)
```

**Pros**:
- Clear separation of concerns (frontend and backend are independent)
- Frontend can be deployed on a CDN (faster static asset delivery)
- Frontend and backend can be versioned/deployed independently
- Easier to swap frontend framework later (e.g., React → Vue)

**Cons**:
- Two separate deployments (more Coolify services, more CI pipelines)
- CORS configuration required (BFF must allow frontend origin)
- Cloudflare Access must protect both frontend and BFF (or frontend is public, BFF is private)
- More complex local development (two processes to run)
- Does not match the design's "minimal admin app" intent (design says "deploy the admin app privately")

**Effort**: High
- Same as Approach 1, plus:
- Separate Dockerfile for frontend (or Cloudflare Pages config)
- Separate Compose service for frontend
- CORS middleware in BFF
- Cloudflare Access policy for both frontend and BFF
- **Total**: ~2000-2800 lines

### Approach 3: Blazor Server (Alternative)

**Description**: Replace React with Blazor Server. Single ASP.NET Core app with real-time UI over SignalR.

**Pros**:
- Single technology stack (.NET only)
- No Node.js toolchain required
- Real-time UI updates (SignalR)
- Easier for .NET developers

**Cons**:
- Blazor Server requires persistent SignalR connection (not ideal for admin UI with low concurrency)
- Larger Docker image (Blazor Server runtime)
- Smaller ecosystem (fewer UI components than React)
- Not a good fit for a "minimal admin app"
- Cloudflare Access integration is less straightforward (SignalR WebSocket must pass through Cloudflare)

**Effort**: Medium
- Similar to Approach 1, but:
- No React/Vite setup
- Blazor components instead of React components
- SignalR hub for real-time updates
- **Total**: ~1200-1800 lines

## Recommendation

**Approach 1: ASP.NET Core BFF + React SPA**

**Rationale**:
1. **Matches the design intent**: The client-administration design explicitly says "deploy the minimal admin app privately" — a single container with BFF + static files is minimal.
2. **Security**: BFF holds all secrets (assertion signing key, HMAC secret). Browser never sees them. Cloudflare Access protects the BFF.
3. **Reuse**: `CloudflareAccessValidator` and `AdminAssertionIssuer` are already implemented and tested. The BFF just wires them into an HTTP pipeline.
4. **Deployment simplicity**: Single container, single Coolify service, single CI pipeline (with Node.js step for React build).
5. **Scalability**: BFF is stateless. Horizontal scaling is trivial (add more containers behind a load balancer).
6. **Future-proof**: If the admin app grows (e.g., adds more features), the BFF can absorb them without changing the frontend deployment.

**Why not Approach 2**: Separate frontend deployment adds complexity (two services, CORS, Cloudflare Access for both) without clear benefit for a minimal admin app serving 1-2 administrators.

**Why not Approach 3**: Blazor Server is not a good fit for a low-concurrency admin UI. React has a larger ecosystem and is more appropriate for a CRUD-heavy admin interface.

## Dropbox Ingestion Boundary

### Future Workflow (Out of Scope for This Exploration)

The user intends a future one-time historical ingestion from local Dropbox-synchronized folders. This is NOT continuous synchronization — it's a one-time snapshot import. Ongoing ingestion comes from integrations (e.g., n8n, client APIs).

### Boundaries and Interfaces

The admin application should define clean interfaces that a future ingestion workflow could use, without implementing that workflow now.

**Interface 1: File Upload Endpoint**
- The admin app MAY expose a file upload endpoint (e.g., `POST /api/v1/admin/ingestion/upload`) in the future.
- The BFF would proxy this to the API's existing `POST /api/v1/collections/{id}/documents` endpoint.
- The admin app does NOT need to implement this now — the API already supports file uploads.

**Interface 2: Operation Tracking**
- The admin app MAY expose an operation tracking UI (e.g., `GET /api/v1/admin/ingestion/operations`) in the future.
- The BFF would proxy this to the API's existing `GET /api/v1/operations/{id}` endpoint.
- The admin app does NOT need to implement this now — the API already supports operation tracking.

**Interface 3: Collection Selection**
- The admin app already has a client/credential management UI. A future ingestion workflow would need to select a target collection.
- The admin app MAY add a collection browser/selector in the future.
- The API already supports `GET /api/v1/collections` — the admin app would just need to proxy it.

**Key Principle**: The admin app should NOT implement any Dropbox-specific logic. Dropbox folder scanning, file discovery, and snapshot import are concerns of a separate ingestion tool (e.g., a CLI tool, a background worker, or a separate service). The admin app's role is to provide a UI for administrators to manage clients, credentials, and audit logs — and to proxy API calls.

**Separation of Concerns**:
- **Admin App**: Human-facing UI for trusted administrators. Manages clients, credentials, audit logs. Proxies API calls.
- **Ingestion Tool** (future): Automated tool that scans Dropbox folders, discovers files, and uploads them to the API. Could be a CLI tool, a background worker, or a separate service.
- **API**: The single source of truth. Handles file uploads, ingestion pipelines, operation tracking, and retrieval.

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Stale Dokploy references in docs cause confusion | High | Medium | Replace all "Dokploy" references with "Coolify" in this change |
| No frontend tooling experience in the team | Medium | High | Use Vite (minimal config), React (well-documented), TypeScript (type safety). Provide clear README with setup instructions. |
| Cloudflare Access integration is complex | Medium | High | Start with unit tests for `CloudflareAccessValidator` (already done). Add integration test with mock Cloudflare JWT. Document Cloudflare Access setup in deployment guide. |
| BFF becomes a bottleneck | Low | Medium | BFF is stateless and CPU-bound (JWT validation, HMAC signing). Horizontal scaling is trivial. Monitor latency and add more containers if needed. |
| React SPA is too heavy for a minimal admin app | Low | Low | Use React with minimal dependencies (no Redux, no React Router — just fetch API and simple state). Keep the UI simple and functional. |
| Review budget exceeded (400 lines) | High | Medium | Use chained PRs: PR #1 (BFF scaffold + Cloudflare auth), PR #2 (assertion issuer + HMAC signer + API proxy), PR #3 (React SPA scaffold + client UI), PR #4 (credential UI + audit UI), PR #5 (Dockerfile + Compose + CI + docs) |
| E2E testing is complex (Cloudflare Access + BFF + API) | Medium | Medium | Start with unit tests for BFF logic. Add integration tests with mock Cloudflare JWT. Defer E2E tests to a later phase (or use Playwright with mock Cloudflare Access). |

## Open Questions

1. **React vs. Vue vs. Svelte**: React is recommended (largest ecosystem, most tutorials), but the team may prefer Vue or Svelte. Decision needed before implementation.

2. **Vite vs. Create React App vs. Next.js**: Vite is recommended (fast, minimal config), but the team may prefer Next.js (SSR, routing) or CRA (legacy, but well-documented). Decision needed before implementation.

3. **Authentication flow**: How does the browser authenticate to the BFF?
   - **Option A**: Cloudflare Access sets a cookie (`CF_Authorization`) → BFF reads cookie → validates JWT → issues session cookie.
   - **Option B**: Cloudflare Access sets a cookie → BFF reads cookie → validates JWT → issues assertion + HMAC per request (no session).
   - **Option C**: Browser reads Cloudflare cookie → sends to BFF → BFF validates and proxies.
   - **Recommendation**: Option B (stateless, matches the design's "one assertion per request" intent).

4. **Static file serving**: Should the BFF serve the React SPA, or should it be deployed separately?
   - **Recommendation**: BFF serves the SPA (single container, simpler deployment).

5. **E2E testing**: Should we invest in Playwright/Cypress E2E tests, or rely on unit + integration tests?
   - **Recommendation**: Start with unit + integration tests. Defer E2E tests to a later phase (or use them only for critical flows).

## Ready for Proposal

**Yes**. The exploration is complete. The orchestrator should inform the user that:

1. **Recommended approach**: ASP.NET Core BFF + React SPA (single container deployment).
2. **Key decisions needed**: React vs. Vue vs. Svelte, Vite vs. Next.js, authentication flow (stateless vs. session).
3. **Review budget**: The work exceeds the 400-line review budget. Chained PRs are required (5 PRs recommended).
4. **Dropbox ingestion**: Out of scope for this change. The admin app should define clean interfaces (file upload, operation tracking, collection selection) that a future ingestion tool could use, but should NOT implement any Dropbox-specific logic.
5. **Stale docs**: The `docs/deployment/client-administration.md` file references "Dokploy" — this should be corrected to "Coolify" in this change.
6. **Next step**: `sdd-propose` to create a formal proposal with intent, scope, alternatives, and rollback plan.
