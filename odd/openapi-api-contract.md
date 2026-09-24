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
   this first: it decides whether build-time generation is viable at all. → **Resolved**: see
   “Task 1 finding” below. The database is *not* the blocker; startup option validation is.
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

## Task 1 finding — the generation pass needs a host start, not PostgreSQL

Probed on 2026-09-24 with `Microsoft.AspNetCore.OpenApi` 10.0.1 and `Microsoft.Extensions.ApiDescription.Server`
10.0.1 wired experimentally into `Rag.Api` (`builder.Services.AddOpenApi()`,
`<OpenApiGenerateDocumentsOnBuild>true</OpenApiGenerateDocumentsOnBuild>`,
`<OpenApiDocumentsDirectory>$(MSBuildProjectDirectory)/../../docs/api</OpenApiDocumentsDirectory>`).

**The generation pass does start the application host.** `dotnet-getdocument` launches `Rag.Api.dll`, and
`Host.StartAsync` runs: `ValidateOnStart` option validation executes and hosted services start.

| Probe | Configuration | Result |
| --- | --- | --- |
| A | committed config, PostgreSQL unreachable (`127.0.0.1:45999`) | **build failed**, exit 1: `Hosting failed to start` / `OptionsValidationException: JWT authentication configuration is invalid.`, stack ending in `StartupValidator.Validate()` → `Host.StartAsync`. No document written. |
| B | A plus a throwaway RSA keypair in `Jwt__*` environment variables | **build succeeded**, exit 0: `Generating document named 'v1'.` → `docs/api/Rag.Api.json`, 3019 bytes, sha256 `af9d9c4354725e2f1135c6fae0bb26b84450dc2ed7c394dad8f5b67d27cd3c27`. |
| C | B with the target forced (`rm src/Rag.Api/obj/Rag.Api.OpenApiFiles.cache`) | build succeeded, byte-identical document, and **exactly one TCP connection arrived on the PostgreSQL port**: `OperationWorker` polls the database while the document is generated. Its failure is swallowed (`catch` → `LogError` → retry), so generation still exits 0. |

Reproduce: `ConnectionStrings__Rag='Host=127.0.0.1;Port=45999;Database=rag;Username=rag;Password=rag;Timeout=2'`
plus the four `Jwt__CurrentSigningKey__*` / `Jwt__ValidationKeys__0__*` variables, then
`dotnet build src/Rag.Api/Rag.Api.csproj -v n`.

Task 1 work-unit commit: `8af7fd1` — this record only. The experimental wiring used by the probes
(`Directory.Packages.props`, `src/Rag.Api/Rag.Api.csproj`, `src/Rag.Api/Program.cs`) is deliberately **not** in
that commit; task 2 owns it, because its shape depends on the generation-time configuration this finding demands.

What the rest of the plan has to absorb:

1. **PostgreSQL is not required to generate the document.** A build host with no database succeeds; a *hanging*
   host would only add connect-timeout latency to the build. Risk 1 is resolved in favour of “no DB needed”.
2. **JWT key material is required**, because the host starts. `appsettings.json` carries no
   `Jwt:CurrentSigningKey`/`ValidationKeys`, so this wiring fails every `dotnet build` that includes
   `Rag.Api` — `dotnet build Rag.sln` in `ci-pr`, `ci-develop` and `ci-release` (no JWT secrets in CI) and any
   test project that pulls the API in. Task 2 must land the generation-time configuration *in the same slice*, or
   the branch breaks CI.
3. **Hosted services run during generation.** The worker's database poll is non-fatal but is real side traffic;
   the generation configuration should keep DB-dependent wiring out instead of relying on swallowed failures.
4. **Generation is incremental.** `GenerateOpenApiDocuments` declares `Inputs="$(TargetPath)"
   Outputs="obj/Rag.Api.OpenApiFiles.cache"`: deleting `docs/api/Rag.Api.json` alone does **not** regenerate it
   (probe C required removing the cache file). A publisher or a reviewer cannot trust the absence of a diff — the
   target must be forced.
5. **Committed config yields a public-plane-only document** (6 paths). `AdminPlane:Enabled` is absent/false and
   `HistoricalIngestion:Enabled` is `false`, so `HistoricalEndpointExtensions` returns before mapping (line 83)
   and neither the admin nor the historical routes reach the document. Enabling `AdminPlane:Enabled` for generation
   also activates four more `ValidateOnStart` validators (assertion key ring, app auth, audit, operations) that need
   key material. A three-plane document therefore needs a generation-only configuration; that is task 2's first
   move and task 3 confirms the result.
6. **Version note.** `Microsoft.AspNetCore.OpenApi` 10.0.1 pulls transitive `Microsoft.OpenApi` 2.0.0, which raises
   NU1903 (GHSA-v5pm-xwqc-g5wc, high) and adds two warnings to an otherwise clean build. CI does not use
   `-warnaserror` (`Directory.Build.props` and the workflows were checked), so it is not fatal today; task 2 should
   either move to 10.0.12 (the installed runtime) or pin the transitive package.

## Task 2 — generation wiring and its evidence

Work-unit commit: `8255917` (5 files, +61/−12). `OpenApiGenerateDocumentsOnBuild` is defaulted to `false` and set to `true` only when `'$(ASPNETCORE_ENVIRONMENT)' == 'OpenApiGeneration'`; a new `appsettings.OpenApiGeneration.json` enables `AdminPlane:Enabled` and `HistoricalIngestion:Enabled` for that pass only; `AddInfrastructure(configuration, forOpenApiGeneration)` swaps every `.ValidateOnStart()` for `.ValidateOnStartUnlessOpenApiGeneration(...)` and guards both `AddHostedService` calls.

Three implementation traps worth keeping:

1. **Conditioning the property alone does not disable generation.** `Microsoft.Extensions.ApiDescription.Server.targets` contains `<OpenApiGenerateDocumentsOnBuild Condition=" '$(OpenApiGenerateDocumentsOnBuild)' == '' ">$(OpenApiGenerateDocuments)</OpenApiGenerateDocumentsOnBuild>` and `OpenApiGenerateDocuments` defaults to `true`, so an unset property is re-defaulted to `true` after the project body is evaluated. The `false` default line in `Rag.Api.csproj` is load-bearing: without it a plain `dotnet build Rag.sln` fails with 24 errors (`MSB3073`, `dotnet-getdocument` exit code 11) because JWT validation fails inside the launched host. `dotnet msbuild src/Rag.Api/Rag.Api.csproj -getProperty:OpenApiGenerateDocumentsOnBuild` reads `false` normally and `true` with the environment variable set.
2. **`--environment` does not exist in this toolchain.** Both `dotnet-getdocument` and `GetDocument.Insider` lack the flag in 10.0.1 and in 10.0.12 (checked in the packaged `--help` output and in the `.props`/`.targets`); the `OpenApiGenerationEnvironment` property only lands in the 11.0 preview line. The environment therefore has to come from the caller's process environment.
3. **The content root follows the working directory, not the assembly.** Invoking `dotnet-getdocument` directly from the repository root yields a public-plane-only six-path document, because neither `appsettings.json` nor `appsettings.OpenApiGeneration.json` is found there. The publisher in task 4 must use the MSBuild command (which runs in the project directory) or set the working directory to `src/Rag.Api`.

Verified evidence:

| Check | Result |
| --- | --- |
| `dotnet build Rag.sln` with no environment variable | exit 0, `0` occurrences of `GenerateOpenApiDocuments`, `docs/api` untouched |
| `ASPNETCORE_ENVIRONMENT=OpenApiGeneration dotnet build src/Rag.Api/Rag.Api.csproj` from a deleted `docs/api` | exit 0, recreates `docs/api/Rag.Api.json`, 10590 bytes, sha256 `0b187af3e43c619080ec5bebc22c51c2d2717dde6bb737033075bf30c0393ba6`, 18 paths — 6 public, 7 admin, 5 historical |
| PostgreSQL listener on `127.0.0.1:45999` during generation | **0 TCP connections**, no `Npgsql` and no worker-failure line in the log |
| Direct `dotnet-getdocument` run with the working directory at `src/Rag.Api` | byte-identical document (same sha256), also 0 connections |
| `dotnet test Rag.sln` | exit 0 — 756 passed, 0 failed, 0 skipped across five assemblies |
| NU1903 after the version bump | no warning names `Microsoft.OpenApi`; the remaining `SQLitePCLRaw.lib.e_sqlite3` warnings are pre-existing (`Microsoft.Data.Sqlite` 10.0.1, untouched by this branch, reproduced by building `src/Rag.HistoricalLoader.Core` on its own) |

The document is **not** committed yet (task 4) and nothing has been pushed.

The document describes routes, not payloads: 18 paths with path parameters resolved, no `operationId`, one component schema (`TokenExchangeRequest` — the only body bound through the minimal-API binder), no request body for the six endpoints that parse JSON through `ApiEndpointSupport.ReadJsonAsync<T>` or `AdminEndpointSupport.ReadJsonAsync<T>`, `200`-only responses without content schemas, no `components.securitySchemes`, and tags taken from the endpoint class names. Task 3 therefore needs endpoint metadata and a security-scheme transformer, not just a review pass.

---

## Tasks

- [x] 1. Establish whether the document can be generated without PostgreSQL, and record the finding.
- [x] 2. Add `Microsoft.AspNetCore.OpenApi` and build-time generation to `Rag.Api`.
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
