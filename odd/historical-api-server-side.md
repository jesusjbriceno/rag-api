# ODD Tasks — Historical API server side into develop

**Goal:** land the historical ingestion API *server* half (Units 7–8 of `historical-ingestion-rebaseline`) in
`develop`, then generate the integration contract from `develop`.

**Why:** `develop` ships `src/Rag.HistoricalLoader.Engine/Api/HistoricalApiClient.cs`, which calls
`POST /api/v1/historical/collections/{id}/uploads`, `PUT /api/v1/historical/uploads/{id}/content`,
`POST /api/v1/historical/uploads/{id}:commit` and `GET /api/v1/historical/{...}` — routes `develop` does not
serve. Client without server. PR #33 was deliberately scoped to slice 11.prev-e only, so the rest of the change
never landed: the `[x]` boxes of Units 7–8 record *verified* work, not *merged* work.

**Source of truth for this slice:** `origin/wip/adminapp-deployable-dashboard` (commit `92d3fe8`), the snapshot of
the pre-PR-33 worktree.

**Key finding that makes this a safe transplant:** for every file this slice modifies, `develop` is byte-identical
to the snapshot's base (`1b89a6e`) — verified file by file with `git diff --stat 1b89a6e origin/develop -- <file>`.
So the snapshot's version of each pure-historical file *is* exactly `develop + the historical delta`, with no hunk
surgery and no risk of reverting newer work.

**Out of scope / must NOT travel (excluded by the `preserve and isolate` decision in
`openspec/changes/historical-ingestion-rebaseline/adminapp-wip-baseline.md`):** the AdminApp BFF and its tests,
`AdminAuthenticationContract.cs`, `src/Rag.Api/AdminAuthentication.cs`, `Rag.sln` and the test `.csproj` edits (all
of them only reference `Rag.AdminApp.Host`), the Coolify/Compose/CI publication-chain work (`Dockerfile`,
`compose.coolify.yaml`, `scripts/validate-coolify-compose.*`, `scripts/test-*`, `scripts/ci-publication-marker*`,
`.github/workflows/ci-*.yml`, `README.md`, `docs/deployment/coolify.md`), and local state (`.atl/`, `.gitignore`).

Also excluded, deliberately:
- `openspec/changes/historical-ingestion-rebaseline/{tasks.md,apply-progress.md}` — `develop`'s copies are newer;
  the snapshot's are the pre-PR-33 ones and would revert ticks.
- `openspec/config.yaml` — the snapshot flips `strict_tdd: false → true` and fills in the test runner. That is a
  project-wide governance change, not part of this slice: separate decision.

## Include — wholesale from the snapshot

New files:

- `src/Rag.Api/Historical/HistoricalEndpointExtensions.cs`
- `src/Rag.Api/Historical/HistoricalOperationEndpoints.cs`
- `src/Rag.Api/Historical/HistoricalOperationHandler.cs`
- `src/Rag.Api/Historical/HistoricalUploadEndpoints.cs`
- `src/Rag.Api/Historical/HistoricalUploadHandler.cs`
- `src/Rag.Application/Auth/HistoricalScopes.cs`
- `src/Rag.Infrastructure/Migrations/20260828000000_AddServiceClientGrants.cs`
- `src/Rag.Infrastructure/Migrations/20260829000000_AddHistoricalIngestion.cs`
- `tests/Rag.IntegrationTests/Historical/HistoricalApiFactory.cs`
- `tests/Rag.IntegrationTests/Historical/HistoricalUploadApiTests.cs`
- `tests/Rag.UnitTests/Auth/TokenScopeExchangeTests.cs`

Modified files:

- `src/Rag.Api/Program.cs` — `AddHistoricalIngestion()`, `MapHistoricalEndpoints()`, historical authorization
  policies and the uploads rate limiter.
- `src/Rag.Api/appsettings.json` — the historical ingestion configuration section.
- `src/Rag.Infrastructure/IngestionDbContext.cs` — historical entities plus `ServiceClientGrantEntity`.
- `src/Rag.Infrastructure/InfrastructureServiceCollectionExtensions.cs`
- `src/Rag.Infrastructure/CredentialRepository.cs` — `FindGrantAsync`.
- `src/Rag.Infrastructure/JwtAuthentication.cs` — scoped token issuance.
- `src/Rag.Infrastructure/OperationClaimRepository.cs`
- `src/Rag.Application/Authentication.cs` — `AccessToken.Scope`, `ServiceClientGrant`, `TokenExchangeOutcome`.
- `src/Rag.Domain/Operation.cs` — `HistoricalUploadState`, `HistoricalUpload`, `HistoricalProvenance`.
- `src/Rag.Domain/Operation/HistoricalTelemetry.cs` — classify by `operation.WorkloadClass`.
- `tests/Rag.UnitTests/AuthenticationTests.cs`

## Include — hunks only

- `tests/Rag.IntegrationTests/ProtectedApiTests.cs`: the `ProtectedApiFactory(..., bool enableHistoricalIngestion =
  false)` signature and the two historical cases
  (`Historical_scope_exchange_is_rejected_by_the_default_feature_gate`,
  `Historical_scoped_token_is_issued_with_the_legacy_grant_and_denied_on_real_time_routes`).
  **Exclude** the appended `AdminAppHostHealthTests` class and its private factories/handlers: they reference the
  BFF project, which is not part of this slice, so keeping them breaks the build.

## Review Workload Forecast

| Field | Value |
| ------- | ------- |
| Estimated changed lines | 2693 insertions / 29 deletions across 23 files (plus this plan) |
| 400-line budget risk | **High** — 6.7x the budget |
| Chained PRs recommended | **No, and the reason matters** |
| Why no split helps | The natural split is Unit 7 (scoped tokens and service-client grants: `Authentication.cs`, `HistoricalScopes.cs`, `JwtAuthentication.cs`, `CredentialRepository.cs`, migration `20260828`, `TokenScopeExchangeTests`) at roughly 650-700 lines, and Unit 8 (the historical upload/commit/status API: `src/Rag.Api/Historical/**`, migration `20260829`, `HistoricalUploadApiTests`, the `Operation` domain types and the `Program.cs` wiring) at roughly 2000. **Both halves still exceed 400**, so a chain would buy two size:exceptions instead of one without reducing the total review surface. |
| What actually makes the review cheap | This is a transplant of already-verified work, not new design. The claim to check is the **boundary**, not the 2700 lines: no AdminApp file, no `.csproj` and no `Rag.sln`, and `develop` was byte-identical to the snapshot's base for every modified file, so each imported file is `develop + exactly the historical delta`. Re-reading 2700 lines teaches a reviewer less than verifying the include/exclude boundary and that byte-identity claim. |
| Suggested command | `size:exception` for this single PR, granted by the maintainer |

## Tasks

- [x] 1. Branch off `origin/develop` and import the pure-historical files wholesale from the snapshot.
  - Evidence: branch `feat/historical-api-server-side` created from `origin/develop` (`df23feb`); 23 paths imported with
    `git checkout origin/wip/adminapp-deployable-dashboard -- <path>`. Pre-check: all 23 paths exist in the snapshot and
    `git diff --stat 1b89a6e origin/develop -- <12 modified files>` is empty (byte-identical base confirmed).
- [x] 2. Apply only the historical hunks of `ProtectedApiTests.cs`.
  - Evidence: snapshot version taken whole, then the trailing `AdminAppHostHealthTests` block removed (lines 322-467);
    file is 321 lines and byte-identical to the snapshot's lines 1-321. Kept: the
    `ProtectedApiFactory(string connectionString, string contentRoot, bool enableHistoricalIngestion = false)` signature,
    `Historical_scope_exchange_is_rejected_by_the_default_feature_gate`,
    `Historical_scoped_token_is_issued_with_the_legacy_grant_and_denied_on_real_time_routes`, the
    `CreateHistoricalClientAsync` helper, and `using Rag.Application.Auth;`.
    `grep -n "AdminApp" tests/Rag.IntegrationTests/ProtectedApiTests.cs` -> empty (exit 1).
- [x] 3. `dotnet build Rag.sln --configuration Release` with 0 errors.
  - Evidence: `Compilación correcta.` — `0 Errores`, `8 Advertencia(s)`. All 8 warnings are pre-existing `NU1903`
    (SQLitePCLRaw 2.1.11 advisory) on `src/Rag.HistoricalLoader.Core`, `src/Rag.HistoricalLoader.Engine`,
    `tests/Rag.HistoricalLoader.UnitTests`, `tests/Rag.HistoricalLoader.IntegrationTests` — no slice file, so no new warning.
- [x] 4. Run the historical, scoped-token and protected-API tests, then the whole suite.
  - Evidence: focused `--filter "FullyQualifiedName~Historical"` on Rag.IntegrationTests -> 17 passed / 0 failed;
    focused `--filter "FullyQualifiedName~TokenScopeExchange"` on Rag.UnitTests -> 10 passed / 0 failed.
    `dotnet test tests/Rag.UnitTests` -> 134/0. `dotnet test tests/Rag.IntegrationTests` -> 77/0 (PostgreSQL via
    Testcontainers started successfully). `dotnet test Rag.sln` -> 767 passed / 0 failed across 5 suites.
    Discovery confirmed 16 historical integration tests and 10 `TokenScopeExchangeTests`.
- [x] 5. Confirm that no AdminApp file or `.csproj`/`.sln` edit leaked into the slice, and record the evidence.
  - Evidence: the slice is exactly 23 tracked paths (11 added, 12 modified).
    `git diff --cached --name-only` + `git diff --name-only` + `git status --short` filtered on
    `\.csproj$|\.sln$|AdminApp|Dockerfile|compose|README|openspec|\.github|scripts/` -> empty (exit 1).
    `Rag.sln` and every `.csproj` are untouched; `src/Rag.AdminApp.*`, `AdminAuthenticationContract.cs` and
    `src/Rag.Api/AdminAuthentication.cs` are untouched.

### Slice inventory (Step 6 evidence)

Added (11): `src/Rag.Api/Historical/{HistoricalEndpointExtensions,HistoricalOperationEndpoints,HistoricalOperationHandler,HistoricalUploadEndpoints,HistoricalUploadHandler}.cs`,
`src/Rag.Application/Auth/HistoricalScopes.cs`,
`src/Rag.Infrastructure/Migrations/{20260828000000_AddServiceClientGrants,20260829000000_AddHistoricalIngestion}.cs`,
`tests/Rag.IntegrationTests/Historical/{HistoricalApiFactory,HistoricalUploadApiTests}.cs`,
`tests/Rag.UnitTests/Auth/TokenScopeExchangeTests.cs`.

Modified (11): `src/Rag.Api/Program.cs`, `src/Rag.Api/appsettings.json`,
`src/Rag.Application/Authentication.cs`, `src/Rag.Domain/Operation.cs`,
`src/Rag.Domain/Operation/HistoricalTelemetry.cs`, `src/Rag.Infrastructure/CredentialRepository.cs`,
`src/Rag.Infrastructure/InfrastructureServiceCollectionExtensions.cs`,
`src/Rag.Infrastructure/IngestionDbContext.cs`, `src/Rag.Infrastructure/JwtAuthentication.cs`,
`src/Rag.Infrastructure/OperationClaimRepository.cs`, `tests/Rag.IntegrationTests/ProtectedApiTests.cs`,
`tests/Rag.UnitTests/AuthenticationTests.cs`.

Pre-existing dirty file preserved untouched and left unstaged: `.atl/skill-registry.md` (outside this slice's scope).
Nothing was committed or pushed.

## Next after this slice

Generate the OpenAPI contract (public + admin + historical planes) from `develop` and publish it to the vault at
`~/obsidian-vault/03_Projects/rag-api/`.
