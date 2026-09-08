# Proposal: Admin Application

## Intent

Build a browser-based admin UI consuming the existing `/api/v1/admin/*` API. The API is complete but has no consumer. Incremental delivery chosen because the historical migration is needed soon — ship the UI slice that maps to currently exposed endpoints first, defer what the API does not yet offer.

## Scope

### In Scope
- ASP.NET Core BFF: Cloudflare JWT validation, assertion issuance, HMAC signing, API proxy, SPA static files
- React/TypeScript SPA: client create/list/detail; credential issue/list/detail/rotate/revoke with one-time secret confirmation gate; paginated audit viewer
- Historical import UI (request/progress/result) — backed by a future local companion agent, never by browser filesystem access
- Dockerfile `admin` target, Coolify Compose service, CI pipeline
- Replace "Dokploy" → "Coolify" in deployment docs

### Out of Scope
- Client update and client archive — API has no endpoints for these; deferred to a future change
- Any new API endpoint — this change consumes only what already exists
- Dropbox integration, folder scanning, change detection, deletion propagation, scheduled sync
- Ongoing ingestion (external workflows)
- RBAC beyond identity allowlist; E2E browser tests

## Capabilities

### New Capabilities
- `admin-bff-auth`: BFF — Cloudflare validation, identity allowlist, assertion issuance, HMAC signing, API proxy, SPA serving
- `admin-client-management`: Client create, list, detail (no update, no archive)
- `admin-credential-management`: Credential issue, list, detail, rotate, revoke — one-time secret display with explicit save confirmation before dismissal
- `admin-audit-view`: Paginated audit log viewer
- `admin-historical-import`: Request/progress/result UI for snapshot import via future local companion; never accesses local folders
- `admin-deployment`: Dockerfile target, Compose service, CI pipeline, Dokploy→Coolify doc correction

### Modified Capabilities
None — no existing specs.

## Approach

Single ASP.NET Core BFF (`src/Rag.AdminApp.Host/`) serving a Vite-built React SPA. Reuses `CloudflareAccessValidator` and `AdminAssertionIssuer`. Browser never holds secrets. One Coolify container. 800-line budget → chained PRs.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Rag.AdminApp.Host/` | New | BFF host |
| `apps/admin-ui/` | New | React SPA |
| `Dockerfile` | Modified | `admin` target |
| `compose.coolify.yaml` | Modified | `admin` service |
| `.github/workflows/` | Modified | Frontend CI |
| `docs/deployment/*.md`, `README.md` | Modified | Dokploy→Coolify |

## Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Stale Dokploy docs | High | Replace all in this change |
| Review budget exceeded | Medium | Chained PRs |
| Secret dismissed unsaved | Medium | Confirmation gate |

## Rollback Plan

Remove `admin` Compose service and Dockerfile target. Additive only — API untouched, docs revertable independently.

## Dependencies

- Admin API — implemented (`src/Rag.Api/AdminEndpoints.cs`)
- `CloudflareAccessValidator`, `AdminAssertionIssuer` — implemented
- Cloudflare Access policy for admin domain
- Local companion agent (future change)

## Success Criteria

- [ ] Admin authenticates via Cloudflare Access and creates/lists/views clients
- [ ] Credential secret shown once; dismissal blocked until confirmation
- [ ] Audit log viewable with pagination
- [ ] Historical import UI: request/progress/result (companion stubbed)
- [ ] Single-container Coolify deployment, no public exposure
- [ ] Zero "Dokploy" references in deployment docs
