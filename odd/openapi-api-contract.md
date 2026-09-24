# ODD Tasks — OpenAPI contract for the integration surface

**Goal:** produce a machine-readable contract (OpenAPI 3.1) for the surfaces the consuming applications
integrate with, **derived from the code** rather than written by hand, versioned in the repository, and
published for reading next to `TAREAS.md` in the vault.

## Surfaces — decided with the maintainer

**Public plane** (`src/Rag.Api/Program.cs`)

| Method | Route |
| --- | --- |
| `GET` | `/api/v1/health` (plus `/api/v1/health/live`, `/api/v1/health/ready`) |
| `POST` | `/api/v1/auth/token` |
| `POST` | `/api/v1/collections` |
| `POST` | `/api/v1/collections/{collectionId:guid}/ingestions:txt` |
| `GET` | `/api/v1/collections/{collectionId:guid}/operations/{operationId:guid}` |
| `POST` | `/api/v1/retrieval:search` |

**Admin plane**, under the `/api/v1/admin` group (`src/Rag.Api/Admin*Endpoints.cs`)

| Method | Route |
| --- | --- |
| `POST`, `GET` | `/api/v1/admin/clients` |
| `GET` | `/api/v1/admin/clients/{clientId:guid}` |
| `POST`, `GET` | `/api/v1/admin/clients/{clientId:guid}/credentials` |
| `GET` | `/api/v1/admin/credentials/{credentialId:guid}` |
| `POST` | `/api/v1/admin/credentials/{credentialId:guid}/rotate` · `/revoke` |
| `GET` | `/api/v1/admin/audit` |

**Historical plane** (`src/Rag.Api/Historical/`)

| Method | Route |
| --- | --- |
| `POST` | `/api/v1/historical/collections/{collectionId}/uploads` |
| `PUT` | `/api/v1/historical/uploads/{uploadId}/content` |
| `POST` | `/api/v1/historical/uploads/{uploadId}:commit` |
| `GET` | `/api/v1/historical/uploads/{uploadId}` |
| `GET` | `/api/v1/historical/collections/{collectionId}/operations/{operationId}` |

**Explicitly out:** the AdminApp BFF (`src/Rag.AdminApp.Host`). It stays under the `preserve and isolate`
decision and lives only on `origin/wip/adminapp-deployable-dashboard`.

## Approach — derived, not hand-written

A hand-written contract drifts on the first change, so the document is generated from the real endpoint
definitions:

- `Microsoft.AspNetCore.OpenApi` — first-party, .NET 10, OpenAPI 3.1 with JSON Schema draft 2020-12. No
  Swashbuckle and no interactive UI.
- **Build-time** generation with `Microsoft.Extensions.ApiDescription.Server`
  (`OpenApiGenerateDocumentsOnBuild=true`, `OpenApiDocumentsDirectory`), so `dotnet build` emits the document.
- A versioned copy under `docs/api/` so the contract has history and a reviewable diff.
- A publisher script reusing the WebDAV pattern of `~/scripts/openspec-espejo.py` (credentials in
  `~/.hermes/secrets/nextcloud.env`) writing to `_obsidian/Desarrollo/03_Projects/rag-api/`, the same folder as
  `TAREAS.md`, which is visible through the rclone mount at `~/obsidian-vault/03_Projects/rag-api/`.

## Risks and open questions — resolve before promising anything

1. **Generation without PostgreSQL.** Build-time generation starts the app host to discover endpoints. If
   startup demands a database connection, the generation pass needs a configuration that avoids it. Establish
   this first: it decides whether build-time generation is viable at all.
2. **The historical flag ships off.** `src/Rag.Api/appsettings.json` carries
   `"HistoricalIngestion": { "Enabled": false }`. The routes exist but are gated off by default. Decide whether
   the generated document includes them (it should, so consumers can implement) and make sure the document says
   the gate is off by default — otherwise the contract describes endpoints production will not serve.
3. **Security schemes.** Consumers will guess unless the document states them: bearer JWT for the public and
   admin planes, scoped service-client tokens for the historical plane (`HistoricalScopes`). Verify what the code
   actually enforces before writing security schemes; do not document an intent the middleware does not
   implement.
4. **Review workload.** This touches `Directory.Packages.props`, `src/Rag.Api/Rag.Api.csproj`, `src/Rag.Api/Program.cs`,
   plus a script and docs. Keep it one small, single-purpose PR; do not bundle it with Unit 11.
5. **`actionlint` is not installed** on this machine, so if any workflow changes, CI is the only lint.

## Tasks

- [ ] 1. Establish whether the document can be generated without PostgreSQL, and record the finding.
- [ ] 2. Add `Microsoft.AspNetCore.OpenApi` and build-time generation to `Rag.Api`.
- [ ] 3. Generate the document and review it against the three planes: every route present, request and response
  schemas resolved, the historical flag caveat stated, and security schemes matching what the code enforces.
- [ ] 4. Commit the generated document under `docs/api/` and add the publisher script.
- [ ] 5. Publish it next to `TAREAS.md` and verify the read-back through the mount.
- [ ] 6. Open the PR (or direct push — the maintainer's call) and record the evidence.

## Handoff state, 2026-09-24

`develop` = **`afa1c84`**. All three planes are in `develop`: the historical server half landed through PR #34
(`1e57cb8`) and the legacy DOC path was removed through PR #35 (`d58a074`). Nothing blocks this work.

Work on this branch, `feat/openapi-contract`, which exists precisely so this document is preserved and the
OpenAPI change stays a single reviewable slice. Unit 11 (the WPF operator shell, 0/6) is now unblocked but needs
a Windows session: WPF `win-x64` and its UI automation cannot be verified from Linux.
