# Apply Progress: adminapp-deployable-dashboard

## Scope of this batch

Implemented **Unit 1 only** (shared wire contract + validated host shell), per the delegated task. No auth/proxy, Docker, Dokploy, CI, or documentation work was advanced. `compose.coolify.yaml` was not touched. RDD remains disabled.

## Delivery path consumed

- Review Workload Forecast: `Decision needed before apply: Yes`, `Chained PRs recommended: Yes`, `400-line budget risk: High`.
- Parent resolved the delivery path as: implement the first work unit (Unit 1) only, as a single chained-PR slice. Chain strategy remains `pending` (operator choice not required for this single-unit slice).

## Completed tasks (persisted checkbox updates)

Unit 1 tasks in `tasks.md` marked `- [x]`:

- RED contract test (`AdminAuthenticationContractTests.cs`)
- GREEN contract (`AdminAuthenticationContract.cs`)
- REFACTOR alias in `src/Rag.Api/AdminAuthentication.cs`
- RED host configuration test (`AdminAppHostConfigurationTests.cs`)
- GREEN host shell (`Rag.AdminApp.Host` project, `Program.cs`, `Configuration/*`, `Rag.sln`)
- TRIANGULATE host configuration `[Theory]` rows
- REFACTOR collapsed bindable POCOs/adapters into `Configuration/AdminAppHostOptions.cs`

Design-inconsistency items resolved and marked `- [x]`:

- `AllowedAdminSubjects` binding type → `string[]`, ordinal/case-sensitive matching (recorded in `design.md` Decision 7).
- `AdminAuthenticationContract` vs `AdminAuthenticationDefaults` non-collision + alias-for-compatibility (recorded in `design.md` Decision 2).
- Pre-existing scope observation recorded (below).

## Design-inconsistency items still open (belong to later units — not touched)

- `- [ ] Resolve HMAC-signer file placement.` (Unit 2)
- `- [ ] Confirm exact scripts/validate-coolify-compose.* no-touch list.` (Unit 4)
- `- [ ] Lock Dokploy manifest default network policy.` (Unit 4)
- `- [ ] Verify GHCR ghcr.io/jesusjbriceno/rag-adminapp repository existence or auto-create policy.` (Unit 5)

## Pre-existing scope observation (informational, follow-up)

`src/Rag.Api/AdminCredentialReadEndpoints.cs` contains `MapPost("/clients/{clientId:guid}/credentials", IssueCredentialAsync)` despite its `...ReadEndpoints` file name. This is a pre-existing API-surface naming mismatch and is **out of scope** for this change. Recommend a follow-up rename in a separate change.

## Files changed

New:

- `src/Rag.Infrastructure/AdminAuthenticationContract.cs` — shared downstream header names + 1 MiB body limit.
- `src/Rag.AdminApp.Host/Rag.AdminApp.Host.csproj` — Web SDK, net10.0, references `Rag.AdminApp` + `Rag.Infrastructure`.
- `src/Rag.AdminApp.Host/Program.cs` — namespaced composition root (`AddAdminAppHost` → `Build` → `MapAdminAppHost` → `Run`).
- `src/Rag.AdminApp.Host/AdminAppHostServiceCollectionExtensions.cs` — `AddAdminAppHost(IConfiguration)`.
- `src/Rag.AdminApp.Host/AdminAppHostWebApplicationExtensions.cs` — `MapAdminAppHost()` (no-op in Unit 1).
- `src/Rag.AdminApp.Host/Configuration/AdminAppHostOptions.cs` — bindable validated options + adapters.
- `tests/Rag.UnitTests/AdminAuthenticationContractTests.cs`
- `tests/Rag.UnitTests/AdminAppHostConfigurationTests.cs`

Modified:

- `src/Rag.Api/AdminAuthentication.cs` — header constants + body limit aliased to `AdminAuthenticationContract`.
- `Rag.sln` — added `Rag.AdminApp.Host` project + configuration platforms.
- `tests/Rag.UnitTests/Rag.UnitTests.csproj` — added `Microsoft.AspNetCore.App` framework reference + host project reference.
- `openspec/changes/adminapp-deployable-dashboard/design.md` — recorded Decision 2 and Decision 7 resolutions.
- `openspec/changes/adminapp-deployable-dashboard/tasks.md` — Unit 1 checkboxes + resolved design items.

## Test commands run

- `dotnet test tests/Rag.UnitTests/Rag.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~AdminAuthenticationContractTests"` → 2/2 pass.
- `dotnet test tests/Rag.UnitTests/Rag.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~AdminAppHostConfigurationTests"` → 27/27 pass.
- `dotnet test tests/Rag.UnitTests/Rag.UnitTests.csproj --configuration Release` → 146/146 pass.
- `dotnet test Rag.sln --configuration Release` → Companion 101 + Unit 146 + Integration 59 = 306/306 pass.

## TDD Cycle Evidence

| Cycle | Step | Evidence |
| --- | --- | --- |
| Contract | RED | `AdminAuthenticationContract` missing → CS0103 compile failure (7 errors) |
| Contract | GREEN | `AdminAuthenticationContract.cs` created; contract tests 2/2 pass |
| Contract | REFACTOR | `AdminAuthentication.cs` aliased; full solution green (306/306) |
| Host config | RED | `Rag.AdminApp.Host` + `WebApplication` + option types missing → compile failures |
| Host config | GREEN | host shell + config options created; config tests 27/27 pass |
| Host config | TRIANGULATE | 13 `[Theory]` missing-setting rows + duplicate/malformed/mismatch/base-URL/blank-allowlist rejection cases |
| Host config | REFACTOR | POCOs + adapters already collapsed into `Configuration/AdminAppHostOptions.cs`; full solution green |

Note: the host shell was created with the bindable POCOs/adapters already consolidated in the single `Configuration/AdminAppHostOptions.cs` file (the REFACTOR target shape), so the "collapse" step was a structural no-op relative to the initial GREEN; it was still re-verified with a full green run.

## Deviations from design

- None behavioural. `MapAdminAppHost()` is an empty extension in Unit 1 (proxy/health mappings land in Units 2/3), which matches the design's "composition root + additive extension" intent.
- The `AllowedAdminSubjects` allowlist is registered eagerly as `IReadOnlySet<string>` (ordinal comparer) rather than via a `ValidateOnStart` options type; the other four sections use `ValidateOnStart`.

## Workload / PR boundary

Honest changed-line count for this batch ≈ **541 authored lines** (new source + tests + solution/csproj edits), composed of:

- Contract extraction: ~57 lines.
- Host shell + configuration validation: ~480 lines (host project + `Configuration/AdminAppHostOptions.cs` + config tests).

This slightly exceeds the delegated 400-line budget by ~35%. It cannot be split further without separating the RED/GREEN config tests from the validation code they verify (the work-unit-commits skill prohibits test/code separation and line-golfing). Recommendation: treat this single work unit as a `size:exception` (or accept Unit 1 as the natural atomic boundary; the tasks' own forecast already groups Units 1+2+3 into PR 1 at ~700–1000 lines).

## Remaining tasks (exact unchecked lines — deferred to later units)

Unit 2–5 tasks remain `- [ ]` (authentication/proxy, health/Docker, Dokploy/runbook, CI/publication) plus four unresolved design-inconsistency items and the parent-only actions. These are intentionally untouched.

## Structured status produced

`next_recommended: parent-lifecycle` — Unit 1 implementation is complete and locally verified; no further implementation work in this batch. Parent-owned actions (bounded review, pre-commit/pre-push/pre-PR receipt validation, archive) are not started here.

## Remediation batch — `adminapp-host-shell-remediation`

### Objective

Re-run the Unit 1 host-shell work unit after the prior finish was invalidated with: *"Scoped Unit 1 exceeded its 400-line cap and pi-lens reports 15 blocking diagnostics in added host/unit-test files despite the delegated build claim."* This batch includes the already-created host/tests in scope, reconciles the 15 pi-lens blocking diagnostics, and re-verifies. No Unit 2 (proxy/auth), Docker, Dokploy, or CI work was advanced.

### The 15 blocking diagnostics — triaged

pi-lens (csharp-ls LSP) reported 15 🔴 blocking findings across two unit-test files:

| File | Count | Diagnostic | Root cause |
| --- | --- | --- | --- |
| `tests/Rag.UnitTests/AdminAuthenticationContractTests.cs` | 7 | `CS0103` `AdminAuthenticationContract` not found (L10–L15, L21) | Stale LSP snapshot: `src/Rag.Infrastructure/AdminAuthenticationContract.cs` was not yet indexed |
| `tests/Rag.UnitTests/AdminAppHostConfigurationTests.cs` | 8 | `CS0234` `Microsoft.AspNetCore.Builder` (L2), `Rag.AdminApp.Host` (L7/L8); `CS0246` `WebApplication` (L168), `CloudflareAccessHostOptions`/`AdminAssertionHostOptions`/`AdminApiHostOptions` (L137/L138/L140); `CS0103` `WebApplication` (L170) | Stale LSP snapshot: `Rag.AdminApp.Host` project not yet in the LSP's `Rag.sln` graph, and the `Microsoft.AspNetCore.App` `FrameworkReference` not yet applied to the test project |

All 15 are **stale/false positives**, not real compiler errors. Every referenced symbol exists and resolves through the real MSBuild/Roslyn compilation.

### Evidence the 15 diagnostics are stale (not real)

- `dotnet build Rag.sln --configuration Release` → **0 warnings, 0 errors** (clean full build; the new `Rag.AdminApp.Host` project compiles and `AdminAuthenticationContract` resolves in `Rag.Infrastructure`).
- `dotnet test Rag.sln --configuration Release --no-build` → **306/306 pass** (Companion 101 + Unit 146 + Integration 59), including the 29 focused Unit 1 assertions across `AdminAuthenticationContractTests` (2) and `AdminAppHostConfigurationTests` (27).
- The compiler is the same Roslyn engine csharp-ls drives; a 0-error build plus green tests demonstrates the LSP was holding a pre-change project graph (missing the host project reference, the new contract file, and the test project `FrameworkReference`), not genuine missing symbols.

### Real (non-blocking) findings addressed

pi-lens also emitted `CS8019`/`CS8933` hints on the host source. Three of them were **real** — `using` directives already covered by the Web SDK implicit global usings — and were removed; the remaining hints (`Configuration/AdminAppHostOptions.cs` L3, test-file L2/L7/L8) were cascading artifacts of the same stale graph and clear once the project graph is fresh:

- `src/Rag.AdminApp.Host/AdminAppHostServiceCollectionExtensions.cs`: removed `using Microsoft.Extensions.Configuration;` and `using Microsoft.Extensions.DependencyInjection;` (both Web SDK global usings).
- `src/Rag.AdminApp.Host/AdminAppHostWebApplicationExtensions.cs`: removed `using Microsoft.AspNetCore.Builder;` (Web SDK global using).

No behaviour changed.

### Files changed in this remediation

- `src/Rag.AdminApp.Host/AdminAppHostServiceCollectionExtensions.cs` — removed 2 redundant usings.
- `src/Rag.AdminApp.Host/AdminAppHostWebApplicationExtensions.cs` — removed 1 redundant using.
- `openspec/changes/adminapp-deployable-dashboard/apply-progress.md` — this remediation record (merged; prior Unit 1 record preserved above).

### Test commands run

- `dotnet build Rag.sln --configuration Release` → 0 warnings / 0 errors.
- `dotnet test Rag.sln --configuration Release --no-build` → 306/306 pass (Companion 101 + Unit 146 + Integration 59).

### TDD evidence (remediation)

The Unit 1 RED → GREEN → TRIANGULATE → REFACTOR cycles were already recorded in the prior batch and their tests remain in place. This remediation is a **REFACTOR-only** step (removing redundant usings) with the full 306-test suite as the regression net; it introduced no new behaviour, so no new RED test was required. The suite stays green after the refactor.

### Deviations from design

- None behavioural. Unit 1 decisions preserved: `AdminAuthenticationContract` lives in `Rag.Infrastructure`; `AdminAuthenticationDefaults` aliases it; the host shell validates `CloudflareAccess`/`AdminAssertion`/`AdminAppAuth`/`AdminApi`/`AllowedAdminSubjects` with `ValidateOnStart` and adapters; `AllowedAdminSubjects` binds as `string[]` with ordinal/case-sensitive matching.

### Remaining tasks

Unchanged from the prior record: Unit 2–5 remain `- [ ]`, four design-inconsistency items remain open (HMAC-signer placement, Coolify no-touch list, Dokploy network policy, GHCR repo policy), and the parent-only actions remain deferred.

    ### Structured status produced
    
    `next_recommended: parent-lifecycle` — the bounded Unit 1 host-shell slice is implemented, the 15 pi-lens blocking diagnostics are reconciled as stale, and the unit is cleanly built and fully tested. Parent-owned actions (bounded review, pre-commit/pre-push/pre-PR receipt validation, archive) are not started here.
    
    
    ## Reconciliation batch — `adminapp-bff-runtime-reconcile`
    
    ### Objective
    
    Inspect the AdminApp BFF as written before a prior timeout, align `tasks.md` and this file with the actual repository state, and re-run build/test to establish reproducible evidence. Fix only compile/test-blocking errors; do not expand functionality, do not create files, do not implement Compose/CI/docs. If the proxy lacked essential tests, report it as blocked rather than inventing coverage.
    
    ### Outcome
    
    - **No code changes were required.** `dotnet build Rag.sln --configuration Release` is clean (0 warnings, 0 errors) and `dotnet test Rag.sln --configuration Release` passes **319/319** (Companion 101 + Unit 159 + Integration 59).
    - The **Units 2 and 3 core runtime is already implemented**, but in a **consolidated layout** that differs from the per-file plan in `tasks.md`:
      - All proxy/auth/health types (`AdminProxyOperation`, `AdminProxyOperationCatalog`, `AdminProxyRequestSigner`, `AdminProxySignedRequest`, `AdminApiClient`, `CloudflareAssertionHeaderReader`, `AdminSubjectAllowlist`, `CloudflareAdminEndpointFilter`, `AdminApiHealthProbe`) live in `src/Rag.AdminApp.Host/AdminAppHostWebApplicationExtensions.cs` and are registered in `src/Rag.AdminApp.Host/AdminAppHostServiceCollectionExtensions.cs`, with `/health/live`, `/health/ready`, and the nine-operation `/api/v1/admin` proxy mapped from `MapAdminAppHost()`.
      - The BFF unit tests (`AdminProxyOperationCatalogTests` 2, `AdminProxyRequestSignerTests` 3, `AdminIdentityHeaderPolicyTests` 1, `AdminApiClientTests` 7) are consolidated in `tests/Rag.UnitTests/AdminAppTests.cs`; the pre-existing `AdminAppTests` (4) cover `CloudflareAccessValidator` + `AdminAssertionIssuer`.
    
    ### What is genuinely NOT implemented (no evidence invented)
    
    - Endpoint-level integration tests `tests/Rag.IntegrationTests/AdminAppHostProxyTests.cs` and `tests/Rag.IntegrationTests/AdminAppHostHealthTests.cs` — **absent**.
    - The security authorisation gate `CloudflareAdminEndpointFilter`, `AdminSubjectAllowlist`, `CloudflareAssertionHeaderReader`, and the health endpoints have **no direct test coverage**; their production code exists without strict-TDD RED evidence.
    - The Docker `admin` target in `Dockerfile` and its build test — **absent** (no `admin` stage).
    - Units 4 (Coolify/Compose/validator/runbook) and 5 (CI publication chain) — **not started**.
    
    ### Deviations from design
    
    - Consolidated file layout (single `AdminAppHostWebApplicationExtensions.cs` + `AdminAppHostServiceCollectionExtensions.cs`) instead of the planned `Proxy/`, `Auth/`, `Health/` file split.
    - Unit tests consolidated in `tests/Rag.UnitTests/AdminAppTests.cs` instead of the planned per-concern test files.
    
    ### Test commands run (reconcile evidence)
    
    - `dotnet build Rag.sln --configuration Release` → 0 warnings / 0 errors.
    - `dotnet test Rag.sln --configuration Release` → 319/319 pass (Companion 101 + Unit 159 + Integration 59).
    
### Blocked status

`next_recommended: resolve-blockers` — the BFF runtime slice cannot be marked verified because the security authorisation gate and health endpoints lack essential tests (the design's integration tests were never written), and the Docker `admin` target is absent. This batch did not create those tests or files (`no crees ficheros` / `no inventes cobertura`). A follow-up implementation batch, authorised to create files, must write the RED integration tests and the Docker target before the slice advances.

## Reconciliation batch — `adminapp-bff-units-2-3-reconcile`

### Objective

Strict documentary reconciliation of the Units 2–3 AdminApp BFF rows in `tasks.md` against the real repository state and the evidence observed after the `adminapp-bff-runtime-reconcile` batch. Documentation only: no source, Dockerfile, workflow, mirror, or git-state changes.

### Evidence observed (parent-supplied runs, repo artifacts verified by reading)

- **Proxy integration tests (new):** `tests/Rag.IntegrationTests/AdminAppHostProxyTests.cs` exists with 8 `[Fact]`s — all-nine-route 401-with-no-downstream, forged/expired/stranger-claim rejection (401/401/403, no downstream), allowlisted request proxied with a signature reconstructed and validated by the real `AdminMachineProofVerifier`, `If-Match` forwarding only for rotate/revoke, idempotency-key generation/multiple-rejection, downstream transport failure → problem+json 502, response-header allowlist, and health endpoints reachable without admin credentials. Result: 8/8 pass.
- **Health integration tests (new):** `tests/Rag.IntegrationTests/AdminAppHostHealthTests.cs` (`AdminAppHostBffHealthTests`, 3/3) covers anonymous liveness with no downstream contact, readiness probing `{origin}/api/v1/health/live` with no auth headers, and readiness 503 on downstream failure. The non-2xx `[Theory]` (3 rows), connection-refused, timeout, and no-auth-header assertion rows live in `AdminAppHostHealthTests` inside `tests/Rag.IntegrationTests/ProtectedApiTests.cs`.
- **Docker admin target (new):** `Dockerfile` has the admin publish in the `build` stage and a `FROM runtime AS admin` stage (single `COPY`/`USER rag`/`ENV ASPNETCORE_URLS` + `DOTNET_EnableDiagnostics=0`/`EXPOSE 8080`/`ENTRYPOINT dotnet Rag.AdminApp.Host.dll`). `scripts/test-admin-docker-target.sh` (default full mode: static assertions + `docker build --target admin` + `docker inspect` of user/identity/ports/env/labels) is invoked by default from `ci-pr.yml`. Build + inspect green (parent-observed).
- **Solution regression:** `dotnet test Rag.sln --configuration Release` → 837/837 pass after isolating the HistoricalLoader timeout (parent-observed).

### Checkboxes updated in `tasks.md` (Unit 3 only)

- Unit 3 RED health integration tests → `[x]` (coverage split between `AdminAppHostBffHealthTests` in the named file and `AdminAppHostHealthTests` rows in `ProtectedApiTests.cs`; every literal assertion of the row is covered).
- Unit 3 RED admin-target build test → `[x]` (the row's allowed alternative: `scripts/test-admin-docker-target.sh` invoked from `ci-pr.yml`; all pinned invariants asserted).
- Unit 3 GREEN Dockerfile admin target → `[x]` (exact literal shape present).
- Unit 3 TRIANGULATE → `[x]` (full-mode build test green + Release 837/837).
- Unit 3 REFACTOR → `[x]` (admin stage authored already collapsed to the `api`/`operator` shape; structural no-op relative to GREEN, verified by the green build test).

### Unit 2 rows deliberately left `- [ ]` (not literally satisfied)

- **RED `AdminAppHostProxyTests.cs`:** the tests are `[Fact]`s iterating a nine-route table, not one `[Theory]` row per operation, and there is no row proving no-extra-downstream-call for unsupported methods/routes/malformed GUIDs. Covered: method/path/body/semantic-header/returned-status behaviour for the nine operations and the auth-gate rejections.
- **TRIANGULATE proxy rows:** missing assertion → 401 (no downstream), expired → 401, non-member `sub` → 403 (no downstream), allowlisted → downstream captured with real-verifier-validated credentials, downstream unavailable → 502 are covered; blank/multiple `Cf-Access-Jwt-Assertion` → 401 and stripped-forbidden-inbound-headers are covered at unit level (`AdminIdentityHeaderPolicyTests`) but have no integration row.
- **REFACTOR `Auth/` extraction:** `CloudflareAssertionHeaderReader` and `AdminSubjectAllowlist` exist as types but consolidated in `AdminAppHostWebApplicationExtensions.cs`; the planned `src/Rag.AdminApp.Host/Auth/*.cs` file split was not created.

### Deviations documented

1. **New passing coverage against pre-existing runtime.** The integration tests, Docker target, and CI wiring were written after the BFF runtime already existed (pre-timeout implementation); no RED was observed for the covered behaviours and none is invented. Strict-TDD RED evidence exists only for the earlier Unit 1 cycles and the consolidated unit-test suite.
2. **Consolidated layout.** All Unit 2/3 production types remain in `AdminAppHostWebApplicationExtensions.cs` / `AdminAppHostServiceCollectionExtensions.cs`; the planned `Proxy/`, `Auth/`, `Health/` file split is superseded. The Unit 2 REFACTOR row stays open on that basis (extraction not performed, not merely "different file names").
3. **Docker test shape.** The build test is a bash script wired into `ci-pr.yml` (the row's allowed alternative), not a `tests/BuildVerification.Tests/admin-target.bats` suite; its default mode is full `docker build` + `docker inspect`, with a `--static` daemon-independent fallback.
4. **Proxy test shape.** Nine-route `[Fact]` table instead of per-operation `[Theory]`; the unsupported-method/malformed-GUID no-downstream rows are the remaining literal gap.

### Test commands run in this batch

None — documentation-only reconciliation. All run evidence cited above was observed by the parent outside this batch (proxy 8/8, health 3/3, `bash scripts/test-admin-docker-target.sh` build + inspect green, Release 837/837 after isolating the timeout).

### Structured status produced

`next_recommended: parent-lifecycle` — Units 2–3 are reconciled: Unit 3 rows are literally satisfied and checked; the three remaining Unit 2 rows stay unchecked with their exact literal gaps recorded. No code, Dockerfile, workflow, mirror, or git-state change was made. Parent-owned actions (bounded review, receipts, chain decisions) are not started here.

## Closure batch — `adminapp-bff-units-2-3-closure`

### Objective

Documentation-only closure of the Units 2–3 AdminApp rows: mark the three remaining Unit 2 checkboxes in `tasks.md`, mark the four ODD tasks complete, and record the observed evidence. No code, Docker/CI, Git, or mirror changes were made in this batch.

### Evidence recorded (parent-supplied runs; repo artifacts verified by reading)

- **Proxy integration tests:** `tests/Rag.IntegrationTests/AdminAppHostProxyTests.cs` now holds **10/10 passing** `[Fact]`s (up from 8/8 at the prior reconciliation). The two prior literal gaps are closed: `Blank_and_multiple_cloudflare_assertion_headers_are_rejected_without_downstream_call` and `Unknown_routes_wrong_methods_and_invalid_guids_never_reach_the_downstream`. The tests remain `[Fact]`s iterating route tables, not per-operation `[Theory]` rows — shape honestly noted on the checkbox.
- **Release build:** `dotnet build Rag.sln --configuration Release` → **0 errors, 8 NU1903 warnings** (NuGet package-audit vulnerability warnings; not compile diagnostics).
- **Release solution regression:** `dotnet test Rag.sln --configuration Release` → **839/839 pass** (up from 837/837).
- **Auth extraction:** `src/Rag.AdminApp.Host/Auth/CloudflareAssertionHeaderReader.cs` and `src/Rag.AdminApp.Host/Auth/AdminSubjectAllowlist.cs` exist; the consolidated-layout deviation recorded earlier is superseded for this REFACTOR row.

### TDD honesty

No production RED is claimed for this closure. The integration tests were written against the already-implemented BFF runtime (pre-timeout implementation), so no RED was observed for the covered behaviours; strict-TDD RED evidence exists only for the earlier Unit 1 cycles and the consolidated unit-test suite. This matches the caveat already recorded on the Unit 2/3 rows and in the prior reconciliation batch.

### Checkboxes updated in `tasks.md` (Unit 2 only — no other rows touched)

- Unit 2 RED proxy integration tests → `[x]` (10 `[Fact]`s; no-RED caveat and `[Fact]`-vs-`[Theory]` shape noted inline).
- Unit 2 TRIANGULATE proxy rows → `[x]` (all gate/rejection/proxy/502 rows have integration coverage; forbidden inbound header stripping remains covered at unit level — noted inline).
- Unit 2 REFACTOR `Auth/` extraction → `[x]` (both files exist; regression evidenced by the 839/839 Release run).

### Files changed in this batch

- `openspec/changes/adminapp-deployable-dashboard/tasks.md` — three Unit 2 checkboxes → `[x]` with honest reconciliation comments.
- `openspec/changes/adminapp-deployable-dashboard/apply-progress.md` — this closure record.
- `odd/adminapp-bff-runtime-completion.md` — the four ODD tasks → `[x]`.

### Test commands run in this batch

None — documentation-only closure. All run evidence cited above was supplied by the parent (proxy 10/10, Release build 0 errors / 8 NU1903, Release 839/839).

### Structured status produced

`next_recommended: parent-lifecycle` — Units 2–3 rows and the ODD feature tasks are closed with observed evidence. Parent-owned actions (bounded review, receipts, chain decisions, archive) are not started here.

## Reconciliation batch — `adminapp-unit-4-coolify-boundary-closure`

### Objective

Strict documentary reconciliation of the Unit 4 rows in `tasks.md` against observed repository evidence. Documentation only: no code, workflows, deployment docs, `.env.example`, CI, Git, or mirror changes.

### Evidence observed (commands run in this batch unless marked parent-supplied)

- **Paired validator suite:** `python3 scripts/test-validate-coolify-compose.py` → **41/41 OK** (exit 0). Every Unit 4 rejection/acceptance case listed in the rows has a dedicated paired-view test (service set, admin image repository/tag/digest shapes, `build`/`ports`/`expose`/`volumes`/`network_mode`/custom networks, missing required references, literal substitution, `PreviousSecret` literal, `ADMINAPP_FQDN` shapes, healthcheck mismatch, `AdminApi__BaseUrl` origin forms, non-admin ports/host-network/custom-network/public-routing metadata).
- **Driver:** `bash scripts/validate-coolify-compose.sh` → green. Renders the interpolated view on stdin and the `--no-interpolate` source view on fd 3, with an EXIT trap cleaning the temporary source view; success summary includes the additive admin boundary with source-preserving provenance.
- **Guard shell:** `bash scripts/test-compose-coolify-untouched.sh` → green: legacy five-service block (postgres, model-download, llama-cpp, migrate, api) byte-equivalent to `develop`, `admin` present and the sole addition.
- **Static Docker target:** `bash scripts/test-admin-docker-target.sh --static` → green (`static admin target verification passed`). The full build+inspect default mode remains wired into `ci-pr.yml`.
- **CI guard wiring:** verified by reading `.github/workflows/ci-pr.yml`: steps `Verify admin Docker target` (full mode), `Validate production Compose` (driver + py suite), and `Guard legacy Compose services` (untouched guard) are all present.
- **Release regression (parent-supplied):** `dotnet test Rag.sln --configuration Release` → **839/839 pass**.
- **Runbook/docs:** `docs/deployment/adminapp-coolify.md` present — leads with the exact FQDN/Cloudflare Access invariant, covers all ten operator steps, fixes key-rotation order (Cloudflare → assertion → HMAC), documents rollback by previous verified immutable image reference, states external inputs and repo-check limits; no invented FQDN, hostname, team domain, audience, key ID, policy, or subject. `docs/deployment/coolify.md` and `README.md` carry the one-public-AdminApp boundary wording.

### Checkboxes updated in `tasks.md` (Unit 4 rows)

Ten Unit 4 rows marked `- [x]` with reconciliation comments: both RED validator rows (with the no-RED-claimed caveat: tests verified against the implemented validator; RED not separately observable), the three Compose/validator/driver GREEN rows, the fixtures row, the untouched-guard TRIANGULATE row, the runbook GREEN and TRIANGULATE rows, and the `coolify.md`/`README.md` GREEN row.

**One Unit 4 row left `- [ ]` honestly — REFACTOR (YAML anchor consolidation):** the admin env-var list remains a flat inline map; no anchor or equivalent consolidation for the admin env-vars exists (the pre-existing `&rag-environment` anchor covers migrate/api only). The row's re-run clause is satisfied (validator + suite green, observed), but the consolidation itself has no evidence.

### Honest operational limits (not verifiable from the repository)

- **actionlint is not installed locally** (`command -v actionlint` → not found). The workflow files were not lint-validated in this environment; actionlint remains a CI-side or operator-side check.
- **Cloudflare, DNS, TLS, and firewall behaviour are not verifiable from the repo.** The validator, guard, and driver prove repository invariants only; Coolify routing, Cloudflare Access enforcement, DNS resolution, and edge firewall behaviour are external inputs the operator must verify before enabling administrative access (as the runbook's Limits section already states).

### Cosmetic follow-up recorded

Residual pre-existing ARM64 `Dokploy host` phrasing remains in the README image-publication section and the `coolify.md` ARM64 happy path. It describes the operator's host platform, not the all-private/two-image boundary wording the row targeted (that wording is replaced). Recorded here as a cosmetic follow-up; not edited in this batch.

### Test commands run in this batch

- `python3 scripts/test-validate-coolify-compose.py` → 41/41 OK.
- `bash scripts/test-compose-coolify-untouched.sh` → green (byte-equivalence guard).
- `bash scripts/validate-coolify-compose.sh` → green (paired-view driver).
- `bash scripts/test-admin-docker-target.sh --static` → green (static target verification).

Release 839/839 was supplied by the parent and is cited, not re-run here.

### Structured status produced

`next_recommended: parent-lifecycle` — Unit 4 rows are reconciled with observed evidence; the single open REFACTOR polish row is recorded with its exact gap. Parent-owned actions (bounded review, receipts, chain decisions, archive) are not started here.

## Unit 4 refactor closure

`compose.coolify.yaml` now names the sole AdminApp environment map with `&admin-environment`, matching the repository's anchor convention without changing values, the paired rendered/source contract, or any legacy service byte. Post-refactor evidence: `bash scripts/validate-coolify-compose.sh` passed, `python3 scripts/test-validate-coolify-compose.py` passed 41/41, and `bash scripts/test-compose-coolify-untouched.sh` confirmed the legacy five-service block remains byte-equivalent to `develop`.

The eight compose line-length findings remain base-only in legacy services and cannot be reformatted without violating the Unit 4 no-touch and byte-equivalence constraints; they are follow-up work, not a blocker for this refactor closure.

## Reconciliation batch — `adminapp-unit-5-publication-chain-closure`

### Objective

Documentation-only reconciliation of the Unit 5 rows in `tasks.md` against observed static repository evidence, plus replacement of the residual dual/both/four-platform wording in `docs/deployment/coolify.md` with the schema-v3 three-image contract. No script, workflow, Compose, Dockerfile, README, Git, or mirror change.

### Evidence observed (commands run in this batch unless marked otherwise)

- **Workflow contract test:** `bash scripts/test-ci-admin-workflow.sh` → green (exit 0). The script asserts, for both `ci-develop.yml` and `ci-release.yml`: admin rows in the `package` matrix (`component: admin`, `target: admin`, `image: ghcr.io/jesusjbriceno/rag-adminapp`, amd64 paired with `ubuntu-latest`, arm64 paired with `ubuntu-24.04-arm`), an admin row in the `promotion` matrix, and `prune_develop "rag-adminapp"` / `prune_staging "rag-adminapp"` (develop) or `prune_staging "rag-adminapp"` (release) retention calls; plus ci-pr admin Docker-target wiring.
- **Marker suite:** `bash scripts/test-ci-publication-marker.sh` → green. Positive fixture asserts `schema_version: 3`, sorted components `== ["admin", "api", "operator"]`, two platforms per image (six total), and homogeneous metadata; heterogeneous `run_id`, platform tag-digest mismatch, and missing `admin.json` negative fixtures all rejected.
- **Verifier suite:** `bash scripts/test-ci-publication-verifier.sh` → green. Three-image fixture (three indexes, six platform digests); the verifier emitted started/verified notices for all three index signature/SLSA pairs and all six platform signature/SPDX/SLSA triples, plus the expected failed/unknown negative rows.
- **Multiarch and guards:** `bash scripts/test-ci-multiarch-index.sh` → green; `bash scripts/test-ci-publication-guards.sh` → green.
- **Workflows verified by reading:** `ci-develop.yml` and `ci-release.yml` each carry the four admin package rows (amd64/ubuntu-latest, arm64/ubuntu-24.04-arm) and an admin promotion row; `ci-pr.yml` wires `test-ci-publication-guards.sh`, `test-ci-publication-marker.sh`, `test-ci-publication-verifier.sh`, `test-ci-multiarch-index.sh`, `test-ci-admin-workflow.sh`, the admin Docker target verification, and the actionlint step.
- **Marker/verifier scripts verified by reading:** `scripts/ci-publication-marker.sh` requires all three proof files and emits `schema_version: 3` with deterministic ordering; `scripts/ci-publication-verifier.sh` consumes the v3 payload (`.images[]`); a grep found no residual dual/both/four wording in either script.
- **REFACTOR (honest structural no-op):** `scripts/test-ci-admin-workflow.sh` was authored with shared helpers (`extract_matrix_entries`, `entry_field`, per-concern assert functions) invoked for both workflows, so no develop/release duplication emerged and there is nothing to collapse.

### Honest operational limits (external gates, not verifiable from the repository)

- **actionlint is not installed locally** (`command -v actionlint` → not found). It was not run in this environment; it executes in the `ci-pr.yml` runner step (`Lint workflows with actionlint`) and remains a CI/operator-side check.
- **GHCR package existence and visibility** for `ghcr.io/jesusjbriceno/rag-adminapp` — including the public-visibility gate if the first push auto-creates it private — is an operator-owned pre-publication gate.
- **GitHub Actions execution** of `ci-develop.yml` / `ci-release.yml` (real runners, native ARM64, Trivy, cosign, SBOM/provenance, index promotion, marker deployment record) has not happened and is not simulated by the static suites.
- **Real publication and retention** of `rag-adminapp` images and versions in GHCR, and the actual `publication-completion` marker record, remain external outcomes of the first real workflow runs.

### Docs wording replacement (`docs/deployment/coolify.md`)

Residual dual/both/four-platform wording replaced with the schema-v3 contract: "dual-image finalization" → three-image; marker payload now documented as recording exactly `admin`/`api`/`operator` with deterministic ordering, two platforms per image, and homogeneous `source_revision`/`workflow`/`publication_tag`/`signature`; "all four platform manifests" → six platform manifests (three images × two architectures); verification sentence and "Repeat for both" → all three images; image-reference table extended with `RAG_ADMINAPP_IMAGE_REFERENCE` (AdminApp tag selected independently of the coordinated API/operator pair); "Dokploy ARM64 happy path" residual → "Coolify ARM64 happy path" with all three suffixes; rollback phrasing disambiguated to the API and operator variables. No FQDN, digest, tag, or publication evidence was invented; placeholder digests appear only inside the pre-existing `<...-64-lowercase-hex>` placeholder shapes.

### Files changed in this batch

- `docs/deployment/coolify.md` — schema-v3 three-image publication wording (see above).
- `openspec/changes/adminapp-deployable-dashboard/tasks.md` — eight Unit 5 checkboxes → `[x]` with reconciliation comments; Unit 5 intro and header pending-note updated.
- `openspec/changes/adminapp-deployable-dashboard/apply-progress.md` — this closure record.
- `odd/adminapp-publication-chain.md` — the four ODD tasks → `[x]`.

### Test commands run in this batch

- `bash scripts/test-ci-admin-workflow.sh` → green.
- `bash scripts/test-ci-publication-marker.sh` → green.
- `bash scripts/test-ci-publication-verifier.sh` → green.
- `bash scripts/test-ci-multiarch-index.sh` → green.
- `bash scripts/test-ci-publication-guards.sh` → green.

### Structured status produced

`next_recommended: parent-lifecycle` — Unit 5 rows are statically reconciled and checked with observed evidence; actionlint, GHCR visibility, real workflow execution, and real publication/retention remain external gates recorded above. Parent-owned actions (bounded review, receipts, chain decisions, archive) are not started here.
