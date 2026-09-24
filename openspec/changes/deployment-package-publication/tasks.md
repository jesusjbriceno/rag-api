# Tasks: Deployment Package Publication

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~550-700 authored |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → PR 2 → PR 3 → PR 4 |
| Delivery strategy | auto-chain |
| Chain strategy | pending |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

Chain strategy is pending — the orchestrator MUST collect the user's choice (stacked-to-main / feature-branch-chain / size-exception) before sdd-apply. All design threat-matrix rows are `N/A`; RED checks derive from the spec/static contract cases below.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Version metadata + health version + Coverlet | PR 1 | `dotnet test tests/Rag.IntegrationTests/Rag.IntegrationTests.csproj --filter Health` | `dotnet run --project src/Rag.Api` then `curl :8080/api/v1/health` | Revert `Directory.Build.props`, `Dockerfile`, `Program.cs`, test edits |
| 2 | Compose pull-only + validator hardening | PR 2 | `bash scripts/validate-coolify-compose.sh` | `docker compose -f compose.coolify.yaml --env-file .env.example config` | Revert compose + validator + `.env.example` |
| 3 | PR gate + develop pipeline | PR 3 | `actionlint .github/workflows/*.yml` + `zizmor` | N/A — GitHub-hosted; E2E on push | Delete `ci-pr.yml`, `ci-develop.yml` |
| 4 | Release workflow + operator docs | PR 4 | `actionlint .github/workflows/ci-release.yml` | N/A — GitHub-hosted; E2E on tag push | Revert `ci-release.yml` + docs |

## Phase 1: Foundation — Version Metadata (PR 1)

- [x] 1.1 Create `Directory.Build.props` with default MSBuild `Version`/`InformationalVersion` properties overridable by CI.
- [x] 1.2 Add `org.opencontainers.image.version`/`revision` labels to `Dockerfile` build stage; preserve stages.
- [x] 1.3 Pin `coverlet.collector` in `Directory.Packages.props`; add collector to both test `.csproj`.
- [x] 1.4 RED: add failing `AuthApiTests` case asserting `/api/v1/health` returns informational version.
- [x] 1.5 GREEN: expose assembly informational version in `src/Rag.Api/Program.cs` `/api/v1/health`.

## Phase 2: Compose Pull-Only + Validator (PR 2)

- [ ] 2.1 RED: extend `scripts/validate-coolify-compose.py` to reject empty/`latest`/floating tags, app `build:`, repo drift, non-pull startup.
- [ ] 2.2 Replace `build:` with `image: ghcr.io/jesusjbriceno/rag-api:${RAG_API_IMAGE_TAG}` and `rag-operator` equivalent in `compose.coolify.yaml`; add `pull_policy: always`.
- [ ] 2.3 Add `RAG_API_IMAGE_TAG` to `.env.example`; keep `build:` in `compose.dev.yaml`.
- [ ] 2.4 GREEN: run `bash scripts/validate-coolify-compose.sh` until all new checks pass.

## Phase 3: CI Workflows — PR Gate + Develop (PR 3)

- [ ] 3.1 RED: `ci-pr.yml` grants only `contents: read`, no secrets/Sonar/publish; verify via `actionlint` + `zizmor`.
- [ ] 3.2 Create `.github/workflows/ci-pr.yml` (restore → build → test → Compose/actionlint gate).
- [ ] 3.3 RED: `ci-develop.yml` derives tag from `$GITHUB_SHA` after regex validation; reject moved-tag collision.
- [ ] 3.4 Create `.github/workflows/ci-develop.yml` (Sonar begin/build/test/OpenCover/end → package: OCI build, SBOM+provenance, Trivy HIGH/CRITICAL block, cosign sign/attest/verify, digest upload, `develop-{sha}` tags, retention).

## Phase 4: Release Workflow + Docs (PR 4)

- [ ] 4.1 Create `.github/workflows/ci-release.yml` (validate → package → GitHub Release, pre-release flag).
- [ ] 4.2 Update `README.md` and `docs/deployment/coolify.md` with publication, verification, pinning, rollback contract.
