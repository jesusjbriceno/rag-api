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

## Task 3 — plan: a typed contract, in three work units

Scope decided with the maintainer: the document must describe payloads and security, and response schemas must not be able to lie, so the anonymous results become named response DTOs. Three work units, each its own commit on this branch and independently verifiable; the PR-or-push shape stays task 6's decision.

**Hard constraint — the wire shape is the contract.** Public and historical responses are anonymous `snake_case` literals (`document_id`, `document_version_id`, `upload_id`, `queue_wait`, …) while admin responses are named records serialized camelCase. A named response record must therefore carry the same `[JsonPropertyName]` names the literal published, or the change silently breaks every consuming app. `CollectionRepresentation` and `SemanticRetrievalMatch` already match camelCase and are safe.

**U1 — characterization tests for the shapes about to change.** `tests/Rag.IntegrationTests` pins much of the surface, but not all of it. Before any DTO moves, pin: the `POST /api/v1/retrieval:search` success payload (every `SemanticRetrievalMatch` field), the `ingestions:txt` `document_id` and `document_version_id`, the token body's `token_type` and `expires_in`, the historical `PUT` `declared_bytes`, and the admin nullable fields (`expiresAt`, `lastRotatedAt`, `revokedAt`, `state`) plus `nextCursor` beyond the audit route. Commit: tests only, no production change.

U1 done — commit `0d94422` (7 test files, +256/−19). Every pinned shape compares complete ordinal-sorted key sets, so a renamed, added or dropped key fails: the retrieval match object, the TXT ingestion body, the token body, the published upload, the credential object across issue/list/get/rotate/revoke, and the admin page objects. The suite went from 756 to 760 tests, 0 failures. Two notes for the reviewer: `ProtectedApiFactory` now substitutes a deterministic `IEmbeddingProvider` double, so the retrieval happy path no longer calls llama.cpp, and `AuthApiTests` moved into the PostgreSQL collection because the token path needs the credentials table. The scoped-token body key set (`access_token`, `token_type`, `expires_in`, `scope`) is still unpinned — no test obtains a scoped body today — and U2 should pin it while it types that response.

**U2 — response DTOs that preserve the wire shape.** Replace the anonymous results with named records: public health, token, ingestion and operation status; historical upload reserve, content, commit, get and operation telemetry; leave the admin records as they are. Every snake_case member carries `[JsonPropertyName]`, and U1's tests must stay green *without edits* — that is the proof that the shape did not move. Commit: code plus tests.

U2 done — commit `8a7cba4` (six files, +168/−72). The public and historical planes now return named records (`PublicResponseContracts.cs`, `HistoricalResponseContracts.cs`); every snake_case member carries an explicit `[JsonPropertyName]`, `TokenResponse.Scope` is the only member marked `WhenWritingNull` because the unscoped body must keep exactly three keys, and nullable members elsewhere keep their keys present. The pre-existing key-set pins passed **unmodified** — that is the proof the wire shape did not move — and one appended test pins the scoped token body, closing the gap U1 left. `Results.*` calls, status codes, `IResult` return types, control flow and the admin plane are untouched, and the regenerated document is byte-identical to task 2's (`0b187af3…`, 18 paths), which confirms the unit added no OpenAPI metadata. Suite: 761 passed, 0 failed. Discovery worth keeping: the TXT ingestion response's `operation_id` is a nullable `Guid` (`AcceptTxtIngestionResult.OperationId`), so the record mirrors `Guid?` to keep serializing `null`.

**U3 — endpoint metadata and security schemes.** `WithName` for operation ids; `WithTags` per plane (`public`, `admin`, `historical`) instead of the endpoint class names; `Accepts<T>` for the six hand-parsed bodies; `Produces<T>` and `ProducesProblem` per status code the code can actually return; the `If-Match` header on rotate and revoke and `Idempotency-Key` on the admin mutations. A document transformer declares `bearerAuth` (HTTP bearer, JWT) for the public and historical planes, and the admin plane's real scheme: six required headers (`X-Admin-App-Id`, `X-Admin-Key-Id`, `X-Admin-Timestamp`, `X-Admin-Signature`, `X-Admin-Assertion`, `Idempotency-Key`), expressible only as `apiKey` headers with the HMAC canonicalisation described in prose. The document must also state, where consumers read it, that the historical routes ship gated off (`HistoricalIngestion:Enabled=false`), that public routes reject scoped tokens (the fallback policy assertion), that historical routes require the exact scope strings, and that `/api/v1/health`, `/live` and `/ready` answer health payloads, not JSON — the two `MapHealthChecks` routes are outside the document today.

U3 split in two when the metadata work turned out larger than the plan estimated, and the first half is done — commit `9824dd3` (three files, +420/−1). `OpenApiDocumentContract` now sets `info.version` from the assembly's informational version and publishes the plane contract, declares the seven schemes the middleware really uses (`bearerAuth` plus one `apiKey` header scheme per signed admin header), and derives each operation's `security` from the endpoint's own authorization metadata, so the document cannot overstate enforcement. The health probes were **not** forced into the paths: `ApiExplorer` skips `MapHealthChecks` endpoints unless `HttpMethodMetadata` and a synthetic `MethodInfo` are added, and the former also narrows routing to GET — a runtime change in a contract-only unit. They are named in `info.description` instead as plain-text anonymous probes, and a test asserts exactly that. Verified against a forced regeneration (the target is incremental, so the document had to be produced by removing `obj/Rag.Api.OpenApiFiles.cache`): 20 operations, no operation referencing an undeclared scheme, no declared scheme left unused, `info.version` `0.1.0-rc.1`. Suite: 773 passed, 0 failed.

U3b remains, and the verifier listed what the document still does not state: `429` on `POST /api/v1/auth/token` and on the historical uploads route (both rate limited); the non-200 success codes (`201` for collections and the admin creates, `202`/`200` for TXT ingestion, `201`/`200` for the historical reserve); the applicable problem responses (`400`, `404`, `409`, `413`, `415`, `422`) per operation; request bodies for the six endpoints that parse JSON by hand (`Accepts<T>`); and the `If-Match` header on the admin credential rotate and revoke routes. Two assertions also need tightening: the scheme test tolerates declared-but-unused schemes, and the historical/anonymous helpers inspect only the first verb.

U3b split again for the same reason, and its first half is done — commit `8234a63` (nine files, +299/−37). All 20 operations now declare an `operationId`, exactly one plane tag and the success codes their handlers can return; the plane tag is assigned by the operation transformer from the same policy decision that picks the security requirement, so the class-name tags disappeared and only `Public`, `Admin` and `Historical` remain in `tags`.

The finding that shaped this unit: **`Accepts<T>` is not document-only metadata.** It is `IAcceptsMetadata`, and `RequestDelegateFactory` enforces the content type before the handler runs, so adopting it in production replaced these endpoints' own `application/problem+json` 415 with a bodyless one and failed `ProtectedApiTests.Collection_and_txt_ingestion_enforce_owner_and_input_contracts`. Rather than change an error contract inside a slice that only documents it — and rather than hand-build request schemas, since `OpenApiSchemaService` exposes no public schema-generation API — the eight request bodies are declared through a generation-gated helper. The document states them; production keeps its 415. Adopting `Accepts<T>` for real is a separate decision.

Verified independently: 18 paths, 20 operations with unique ids, exactly one plane tag each, every declared 2xx set matching its handler, the eight request bodies with their content types, and no `415` or `429` declared yet. Suite: 803 passed, 0 failed. Residual gap recorded for the next half: the path helpers in the test class recognise only `get`, `post`, `put`, `delete` and `patch`, so a `HEAD`, `OPTIONS` or `TRACE` verb added to a documented path would go uninspected.

U3b closed in three more commits. `dc71296` declared the non-success responses of all 20 operations, each traced to a named exception or return, and separated the two `415` shapes (the endpoints' own `application/problem+json` versus the framework's bodyless rejection of a bound body). Independent verification then found the matrix over-declaring a `403` on the nine admin operations — the admin policy only requires an authenticated user, so no path produces it — and mis-shaping the authentication responses; `3a0d58d` fixed both, corrected a false assumption of mine (the rate limiter does **not** set `Retry-After`; only the historical quota and watermark paths do), and turned the two multi-shape statuses into descriptions that name both branches. `38f77bc` added the `Retry-After: 1` that four admin `409` conflicts write through `AdminProblem.Create`, and tightened the assertion to exact `(operation, status)` pairs.

Two limits worth keeping in view. The shared admin `409` also covers conflicts with no retry hint, so its header description scopes the hint to the in-progress case: OpenAPI cannot express per-condition headers on one status. And the document is generated from a variant of the app — the generation environment enables the two configuration gates and declares the request bodies that production does not enforce through `Accepts<T>` — so it describes the contract, not the wiring of a given host.

Settled during U3. The two health-check routes stay out of `paths`: `ApiExplorer` skips `MapHealthChecks` endpoints unless routing metadata is added, and the metadata that works also narrows them to GET, so they are named in `info.description` as plain-text anonymous probes and a test asserts exactly that. `info.version` now tracks the assembly's informational version (`0.1.0-rc.1`), the same value `GET /api/v1/health` reports, instead of the `1.0.0` default `AddOpenApi` produces.

**Review workload:** task 3 landed as eight commits — U1 pins (256 lines of tests), U2 DTOs (168), U3a contract and security (420), U3b-1 identity and success contracts (336), U3b-2 the problem matrix (478), U3b-3 the authentication accuracy fix (~180), U3b-4 the admin retry hints (36) and their records. U3b-2 and U3a are the two that exceed the plan's 400-line budget on their own; if task 6 wants a smaller review, U3b-2 is the natural second PR.

---

Task 4 and 5 are closed. The committed artifact is reproducible — regenerating it into another directory from the same commit yields the same bytes (`sha256 0509d102`) — and it must be regenerated by forcing the incremental target, because `obj/Rag.Api.OpenApiFiles.cache` makes the generator skip silently. `scripts/publish-openapi-contract.py` publishes it through WebDAV to the same vault folder as `TAREAS.md`; the read-back through the mount matched the repository copy byte for byte on the first attempt, so the contract the consuming applications read is the one this branch produced. Task 6 closed with issue #36 and PR #37 to develop; the remaining decision left on purpose is whether to adopt Accepts<T> in production, which would change the 415 response body of seven endpoints and needs its own slice.

## Tasks

- [x] 1. Establish whether the document can be generated without PostgreSQL, and record the finding.
- [x] 2. Add `Microsoft.AspNetCore.OpenApi` and build-time generation to `Rag.Api`.
- [x] 3. Generate the document and review it against the three planes: every route present, request and response
  schemas resolved, the historical flag caveat stated, and security schemes matching what the code enforces.
- [x] 4. Commit the generated document under `docs/api/` and add the publisher script. — artifact committed from the current contract; publisher in `scripts/publish-openapi-contract.py`.
- [x] 5. Publish it next to `TAREAS.md` and verify the read-back through the mount. — published through scripts/publish-openapi-contract.py (HTTP 201); the mount read-back is byte-identical to the repository artifact (sha256 0509d102).
- [x] 6. Open the PR (or direct push — the maintainer's call) and record the evidence. — issue #36 (`type:feature`, `status:approved`); PR #37 to `develop`, branch pushed, contract published.

## Handoff state, 2026-09-24

`develop` = **`afa1c84`**. All three planes are in `develop`: the historical server half landed through PR #34
(`1e57cb8`) and the legacy DOC path was removed through PR #35 (`d58a074`). Nothing blocks this work.

Work on this branch, `feat/openapi-contract`, which exists precisely so this document is preserved and the
OpenAPI change stays a single reviewable slice. Unit 11 (the WPF operator shell, 0/6) is now unblocked but needs
a Windows session: WPF `win-x64` and its UI automation cannot be verified from Linux.
