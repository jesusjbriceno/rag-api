# Admin API Key MVP

## Objective

Provide a deployable, narrowly scoped browser application for an authorized administrator to create a customer client and issue its first API credential, using the existing administration API.

## Problem and rationale

The main branch already contains the administrative API and the Cloudflare JWT/assertion primitives, but it has no executable admin host, key-management UI, allowlist guard, or admin deployment service. This blocks onboarding customers through a browser. The previous AdminApp work is recorded as interrupted and is not the implementation base; this feature starts from the current `main` commit on a new feature branch.

## User-authorized scope

- Validate Cloudflare Access JWTs at the BFF and fail closed unless the verified opaque subject is on a configured administrator allowlist.
- Keep assertion private keys, API HMAC credentials, and customer API secrets server-side. The browser never receives upstream signing material.
- Expose only the existing operations needed for the first key workflow: create a client and issue a credential for that client. Do not change `Rag.Api` endpoints or authorization. Each logical browser mutation supplies one UUID `Idempotency-Key`; the BFF validates and forwards that exact key so retries do not create duplicate clients or credentials.
- Provide a minimal React/TypeScript UI for creating a client and issuing its credential. Display the secret once in volatile UI state, and require explicit save confirmation before dismissing it.
- Add the admin image/service and CI checks, plus deployment documentation that names required configuration without inventing real Cloudflare values or secrets.
- Do not perform an actual Coolify deployment, publish images, push, create a PR, or merge.

## Out of scope

- Historical import/companion flows, audit UI, client edit/archive, credential rotate/revoke, new API routes, RBAC beyond subject allowlisting, Cloudflare policy provisioning, and live deployment.
- Reusing or merging the interrupted WIP branch.

## Constraints and decisions

- Preserve existing admin API and trust contracts. Reuse `CloudflareAccessValidator` and `AdminAssertionIssuer`; add the BFF request signer against the canonicalization implemented by the API.
- Validate Cloudflare identity and allowlist on every browser request; do not trust identity headers supplied by the browser, and do not issue upstream assertions before authorization succeeds.
- Protect browser mutation routes against cross-site requests with same-origin/antiforgery controls.
- Keep the BFF proxy typed and restricted to the two required admin operations; never forward arbitrary browser-selected URLs or headers.
- Generated credential secrets must not be logged, persisted, placed in URL/query parameters, or recoverable after leaving the one-time display.
- Preserve the pre-existing unrelated modification in `.atl/skill-registry.md`.
- Local conventional commits are authorized on this feature branch. Push, PR, merge, and deployment are not authorized.
- Delivery strategy: `auto-chain`; `chain_strategy: feature-branch-chain` selected by the user. The current branch is the feature/tracker branch; future review slices may be prepared locally only. Push, PR, merge, and deployment remain unauthorized.
- Initial forecast of 700–1,100 authored lines was low. Tasks 1–2 currently total 2,279 authored changed lines plus 3,147 generated `package-lock.json` lines, before Task 3. User selected `auto-chain` / `feature-branch-chain`; all commits remain local. Task 1 slices are signer+unit tests (357 lines) and integrated BFF+security tests (960 lines). Task 2 slices are UI flow (754 authored lines) and guarded static serving (208 lines). The generated lockfile is not counted as authored work but remains visible in review; do not code-golf or split tests from behavior.

## Work plan

### Task 1 — Build the Cloudflare-guarded BFF and signed admin bridge

- [x] 1.1 Add a runnable `Rag.AdminApp.Host` project and wire it into the solution.
- [x] 1.2 Add fail-closed per-request Cloudflare JWT validation and an exact subject allowlist; keep health probing separate from browser routes.
- [x] 1.3 Add a server-side request signer and fixed client-create / credential-issue proxy routes with fresh short-lived assertions, HMAC proofs, validated stable idempotency keys, safe response forwarding, and redacted failures.
- [x] 1.4 Add integration coverage for missing/invalid/disallowed identities, allowed requests, both proxied operations, signing contract, stable idempotency-key forwarding/retry, and no-secret/no-arbitrary-proxy behavior.

**Allowed edit surfaces for Task 1**

- `Rag.sln`
- `src/Rag.AdminApp/AdminRequestSigner.cs`
- `src/Rag.AdminApp.Host/**`
- `tests/Rag.IntegrationTests/AdminAppBffTestFactory.cs`
- `tests/Rag.IntegrationTests/AdminAppBffGuardTests.cs`
- `tests/Rag.IntegrationTests/AdminAppProxyTests.cs`
- `tests/Rag.IntegrationTests/AdminAppKeyFlowTests.cs`
- `tests/Rag.IntegrationTests/Rag.IntegrationTests.csproj`
- `tests/Rag.UnitTests/AdminRequestSignerTests.cs`

**Verification**

- `dotnet test tests/Rag.UnitTests/Rag.UnitTests.csproj --filter "FullyQualifiedName~AdminRequestSignerTests"`
- `dotnet test tests/Rag.IntegrationTests/Rag.IntegrationTests.csproj --filter "FullyQualifiedName~AdminApp"`
- Confirm unauthorized requests never reach the fake admin API and verified allowlisted requests carry the API's expected HMAC/assertion contract.

### Task 2 — Add the one-time client/key issuance UI

- [x] 2.1 Add the minimal React/TypeScript app using the BFF only; serve its shell/assets behind the same Cloudflare subject guard (health remains the only unauthenticated probe). Do not call the admin API from the browser.
- [x] 2.2 Implement client creation followed by credential issuance, with a recoverable UI state if client creation succeeds but issuance fails. Generate a UUID `Idempotency-Key` once per logical mutation and reuse it unchanged for retries; use a distinct key for credential issuance.
- [x] 2.3 Show the generated secret only in volatile memory and block dismissal until the administrator confirms it was saved.
- [x] 2.4 Add UI tests for successful issuance, partial failure, secret-once display, confirmation-gated dismissal, and integration coverage that direct unauthenticated/disallowed requests cannot retrieve the SPA shell or `/health/*` subpaths.
- [x] 2.5 Upgrade the dev-only test-runner dependency to Vitest 5.0.3 and verify full/production npm audits, UI tests, build, and AdminApp integration tests.

**Allowed edit surfaces for Task 2**

- `apps/admin-ui/**`
- `src/Rag.AdminApp.Host/**`
- `tests/Rag.IntegrationTests/AdminAppStaticFilesTests.cs`

**Verification**

- `npm --prefix apps/admin-ui ci`
- `npm --prefix apps/admin-ui audit --json`
- `npm --prefix apps/admin-ui audit --omit=dev --json`
- `npm --prefix apps/admin-ui test -- --run`
- `npm --prefix apps/admin-ui run build`
- `dotnet test tests/Rag.IntegrationTests/Rag.IntegrationTests.csproj --filter "FullyQualifiedName~AdminApp"`

### Task 3 — Package and document the deployable MVP

- [x] 3.1 Add a non-root multi-stage admin image that builds the Vite SPA with a supported Node engine and copies only static assets into the .NET host. Add the private Coolify admin service with no direct host port, and wire the API's existing AdminPlane/trust configuration through required deployment variables.
- [x] 3.2 Add PR/release CI build/test coverage and admin image publication configuration for future releases; update exact-service-set compose validation/tests. Build stages and CI must use a supported Vitest 5 engine: Node `^22.12.0 || ^24.0.0 || >=26.0.0` (prefer Node 22 LTS; do not use Node 23/25). No release/image is executed in this task.
- [x] 3.3 Document required configuration, first-admin allowlist onboarding, Cloudflare Access boundary, health verification, secret handling, and rollback without populating real values.
- [x] 3.4 Extend the durable publication completion marker and its marker/verifier regression fixtures to include `adminapp` alongside `api` and `operator`; preserve `schema_version: 2` and existing workflow behavior. The user explicitly authorized the three additional script surfaces after independent verification found the current two-image contract.
- [x] 3.5 Normalize an empty optional `AdminAppAuthOptions.PreviousSecret` to `null` before validation so Compose's empty default does not become an accepted empty HMAC key; add a focused regression test. The user explicitly authorized the two source/test paths after read-only verification confirmed this behavior.

**Allowed edit surfaces for Task 3**

- `Dockerfile`
- `compose.coolify.yaml`
- `.github/workflows/ci-*.yml`
- `scripts/validate-coolify-compose.py`
- `scripts/test-validate-coolify-compose.py` (explicitly approved by the user after the actual existing validator names were discovered)
- `scripts/ci-publication-marker.sh` (explicitly approved by the user after independent verification found the two-image completion contract)
- `scripts/test-ci-publication-marker.sh` (explicitly approved by the user)
- `scripts/test-ci-publication-verifier.sh` (explicitly approved by the user)
- `src/Rag.Infrastructure/AdminAppAuthOptions.cs` (explicitly approved by the user after the empty-previous-secret finding)
- `tests/Rag.UnitTests/AdminMachineProofVerifierTests.cs` (explicitly approved by the user)
- `tests/Rag.IntegrationTests/*Compose*Tests.cs`
- `tests/Rag.UnitTests/*Compose*Tests.cs`
- `docs/deployment/adminapp-mvp.md`

**Verification**

- `npm --prefix apps/admin-ui ci`
- `npm --prefix apps/admin-ui audit --json`
- `npm --prefix apps/admin-ui test -- --run`
- `npm --prefix apps/admin-ui run build`
- `dotnet test tests/Rag.UnitTests/Rag.UnitTests.csproj`
- `dotnet test tests/Rag.UnitTests/Rag.UnitTests.csproj --filter "FullyQualifiedName~AdminMachineProofVerifierTests"`
- `dotnet test tests/Rag.IntegrationTests/Rag.IntegrationTests.csproj --filter "FullyQualifiedName~AdminApp"`
- `python3 scripts/test-validate-coolify-compose.py`
- `bash scripts/test-ci-publication-marker.sh`
- `bash scripts/test-ci-publication-verifier.sh`
- `bash scripts/validate-coolify-compose.sh`
- `python3 scripts/validate-workflow-checkout.py`
- `docker compose -f compose.coolify.yaml config --quiet`
- `docker build --target admin -t rag-adminapp-mvp:local .`
- No deployment or release/publication command.

## Acceptance criteria

- Only a verified Cloudflare Access identity whose opaque subject is explicitly allowlisted can reach BFF browser operations or retrieve the SPA shell/assets; health remains the only unauthenticated probe.
- Cross-origin mutation attempts are rejected without forwarding an API request.
- The BFF signs upstream requests server-side and only proxies the two fixed operations required by the UI.
- An authorized administrator can create a client and issue its first API credential through the UI.
- The secret is visible once, absent from persistent browser storage and logs, and cannot be dismissed until explicit save confirmation.
- The admin service can be built and is represented in Coolify Compose/CI without opening an unintended host port.
- Admin UI build and test environments satisfy Vitest 5's Node engine range (`^22.12.0 || ^24.0.0 || >=26.0.0`).
- No API behavior, live deployment, image publication, push, PR, or merge is performed.

## Progress and evidence

- Base: `main` at `4e5eb403070d3380ebdac0b8824313815cf2b3fd` (local `origin/main` ref matched at exploration time; no fetch performed).
- Branch: `feat/admin-api-key-mvp`.
- Pre-existing worktree change: `.atl/skill-registry.md` (preserved; not part of this feature).
- Prior AdminApp WIP is recorded as interrupted and not merged; it was not reused.
- Task 1 complete. Commits: `7dfc0915c8765b8ddede1f55abb1942459c3bd42` (`feat(admin): add server-side request signing`, 357 lines); `a64985d4c609b5e8da1092536805b3fc631f2eab` (`feat(admin): add Cloudflare-guarded admin BFF`, 960 lines).
- Task 1 independent verification: signer tests 9/9; AdminApp integration tests 18/18. The follow-up writer reported four idempotency regressions RED and GREEN after the fix; independent verifier observed the final GREEN only.
- Task 1 native ASSESS was `unassessable` because candidates were untracked (RDD off), so the required independent verifier ran; residual risks are exact-Origin-only CSRF and no server log-capture test for secret non-logging. API duplicate suppression was not re-exercised; Rag.Api remained out of scope.
- Task 2 implementation and verification complete. Commits: `48360571b2018cba6ab92553e0a01954e023922e` (`feat(admin): add one-time client credential UI`, 754 authored lines plus 3,147 generated package-lock lines); `26ad2763ed9041503d62606d0c35eb8c5f477558` (`fix(admin): guard SPA static assets`, 208 lines).
- Task 2 checks independently passed: `npm ci`; full and production-only npm audits (0 vulnerabilities); UI tests 4/4; production build; AdminApp integration tests 25/25. Static shell integration requires the UI build to run before the .NET integration suite because tests read `apps/admin-ui/dist`.
- `/health/*` regression was reported RED (unauthenticated shell returned 200) and GREEN after the guard exemption was narrowed to exact `/health`; the independent verifier observed the corrected suite green. `/api` unmatched paths remain plain 404.
- Vitest 5.0.3 removed the vulnerable Tinypool tree. Its supported Node engine range is `^22.12.0 || ^24.0.0 || >=26.0.0`; prefer Node 22 LTS for CI and Docker build stages.
- User selected `auto-chain` / `feature-branch-chain` and authorized local conventional commits only. No push, PR, merge, or deployment is authorized.
- Native ASSESS was unassessable while candidates were untracked (RDD off); independent verifiers followed the risk-gated path. Engram mirror remains pending because the installed core lacks `root_session_resume`; this local document is authoritative until Engram is upgraded.
- Task 3.1–3.5 are complete. Final independent verification passed all 14 listed commands: npm CI/audit/test/build (0 vulnerabilities, UI 4/4), .NET unit tests 145/145, focused machine-proof tests 12/12, AdminApp integration 25/25, Compose validator tests 25/25, marker/verifier tests, Compose validation/config, workflow-checkout validation, and local admin Docker build. `git diff --check` passed.
- Task 3 test-first evidence: Compose validator 25 tests RED→GREEN; marker and async-verifier fixtures RED→GREEN for three-image proof and missing-admin failure; empty `PreviousSecret` tests RED (2 failed/10 passed)→GREEN (12/12), preserving non-empty rotation and equal-secret rejection.
- Task 3 work unit A: `5a7ed732bdce5932457b6b876ea0b1f1005e65a1` (`feat(admin): package private Coolify service`, 304 insertions/6 deletions; Docker, Compose, validator/tests, API empty-secret normalization/tests).
- Task 3 work unit B: `95240688d65a92e44b8f5eb26fb114ae037fdb93` (`ci(admin): publish and verify admin image`, 286 insertions/15 deletions; PR/release workflows, three-image marker/tests, deployment runbook).
- Runbook documents API `current=new` + `previous=old` before switching BFF to new, waits 60 seconds (current fixed API default), and removes previous only after overlap; empty optional values normalize to `null` and no longer act as an empty HMAC key.
- Native ASSESS returned `unassessable` because the local ODD task/runbook files are untracked; risk-gated independent verification passed. Native `inspect` returned terminal `rdd_disabled`, so no native review started. `actionlint` was unavailable locally; workflow-checkout validation passed and PR CI runs actionlint.
- No actual image publication, push, PR, merge, or deployment occurred. `.atl/skill-registry.md` remains the preserved pre-existing modification; `odd/tasks/admin-api-key-mvp.md` remains a local untracked ODD record. Engram mirror is still pending because the installed core lacks `root_session_resume`.
