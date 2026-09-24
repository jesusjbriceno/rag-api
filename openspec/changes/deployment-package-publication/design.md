# Design: Deployment Package Publication

## Technical Approach

Add three least-privilege GitHub Actions workflows around the .NET solution and two-target Dockerfile. PRs validate only; `develop` adds SonarQube and API/operator pre-releases; semver tags publish releases. Production Compose becomes pull-only while the development override keeps builds.

## Architecture Decisions

| Option | Tradeoff | Decision |
|---|---|---|
| One image vs. existing API/operator targets | One image requires the deferred Dockerfile redesign. | Publish `ghcr.io/jesusjbriceno/rag-api` and `rag-operator` with one coordinated immutable tag. |
| Push then sign vs. final-tag-last | Cosign signs registry digests, but consumers must never see an unverified final tag. | Build/attest/scan local OCI archives, upload clean candidates by digest, sign and verify by digest, then create the final tag. “Publish” means final-tag creation. |
| Separate coverage and analysis jobs vs. one quality job | Artifacts add complexity and Sonar begin/end must bracket compilation. | On `develop`, one job runs scanner begin, Release build, tests producing OpenCover, then scanner end/quality gate. |
| Floating channels vs. immutable tags | Floating tags simplify deployment but destroy rollback identity. | Use only `develop-<40-char-sha>` and exact `vX.Y.Z[-prerelease]`; never `latest`. |

## Workflow and Data Flow

```text
PR -> validate(restore -> build -> test -> Compose/actionlint)
develop -> quality(Sonar begin -> build -> test/coverage -> end) -> package
tag v* -> validate -> package -> GitHub Release
package: derive/validate tag -> build API+operator OCI -> SBOM+provenance
         -> Trivy -> digest upload -> cosign sign/attest/verify -> collision check
         -> final tags -> retention
```

Buildx emits OCI archives plus max-mode provenance; Syft emits SPDX JSON; Trivy scans each archive with `HIGH,CRITICAL` and exit code 1. Both digests, SBOMs, provenance, and cosign bundles remain paired. A release record follows verification; `v0.1.0-rc.1` uses GitHub's pre-release flag.

## Security, Failure, and Rollback

PR jobs receive `contents: read` and no secrets. Sonar credentials (`SONAR_TOKEN`, `vars.SONAR_HOST_URL`) exist only in the trusted `develop` quality job. Package jobs receive job-scoped `contents: read`, `packages: write`, and `id-token: write`; releases alone add `contents: write`. Checkout disables persisted credentials; actions are commit-SHA pinned; shell uses `set -Eeuo pipefail`, quoted values, no `eval`, and derives tags only from GitHub event fields after regex validation.

Any build, analysis, scan, signature, attestation, verification, or Compose failure leaves no final tag or release; untagged candidates are cleaned. Existing tags are idempotent only for the same digest and fail closed otherwise. Rollback sets `RAG_API_IMAGE_TAG` to the prior semver and redeploys; pull failure never falls back to build. Keep semver images indefinitely, the newest 20 `develop-*` versions (at least 30 days), and clean failed candidates after one day without deleting referrers.

## File Changes

| File | Action | Description |
|---|---|---|
| `.github/workflows/ci-pr.yml` | Create | PR validation gate. |
| `.github/workflows/ci-develop.yml` | Create | Develop quality, publication, and scoped retention. |
| `.github/workflows/ci-release.yml` | Create | Semver publication and release record. |
| `Directory.Build.props`, `Dockerfile` | Create/Modify | Default/CI MSBuild version properties and OCI version/revision labels; preserve stages. |
| `Directory.Packages.props`, `tests/Rag.UnitTests/Rag.UnitTests.csproj`, `tests/Rag.IntegrationTests/Rag.IntegrationTests.csproj` | Modify | Pin/add Coverlet collector. |
| `src/Rag.Api/Program.cs`, `tests/Rag.IntegrationTests/AuthApiTests.cs` | Modify | Expose/test assembly informational version in `/api/v1/health`. |
| `compose.coolify.yaml`, `compose.dev.yaml`, `.env.example` | Modify | Pin both GHCR images with one tag; production `pull_policy: always`; retain dev builds. |
| `scripts/validate-coolify-compose.py`, `scripts/validate-coolify-compose.sh` | Modify | Reject build fallback, empty/`latest`/floating tags, repository drift, and non-pull startup. |
| `README.md`, `docs/deployment/coolify.md` | Modify | Publication, verification, pinning, and rollback operator contract. |

## Interfaces / Contracts

`PublicationVersion` is `develop-$GITHUB_SHA` or the exact Git tag; release MSBuild `Version` strips leading `v`, while `InformationalVersion`, OCI `org.opencontainers.image.version`, and health metadata preserve the exact publication value. Compose requires `RAG_API_IMAGE_TAG` matching an immutable semver tag and applies it to both repositories.

## Testing Strategy

| Layer | What / Approach |
|---|---|
| RED/static | Invalid tag injection, moved-tag collision, forbidden PR publish/Sonar permissions, mutable Compose tags, build fallback; run contract validator plus actionlint/zizmor. |
| Integration | Release build/test with OpenCover; local Buildx OCI, SBOM/provenance presence, and Trivy blocking fixture. |
| E2E | Publish disposable develop/release candidates; verify both digests, cosign identity/issuer, referrers, pre-release flag, pull-only Compose, and rollback pin. |

## Threat Matrix

| Boundary | Minimum adversarial cases | Applicability | Response / RED tests |
|---|---|---|---|
| Documentation-like paths | `requirements.txt`/`CMakeLists.txt`/executable-MDX/`README.sh` | N/A: no executable classification | None. |
| Git repository selection | `git -C`/relative/absolute paths | N/A: fixed checkout/cwd | None. |
| Commit state | staged/`commit -a`/empty index | N/A: no commits | None. |
| Push state | tracking/first-push/refspec | N/A: no VCS push | None. |
| PR commands | `--head`/environment-prefix/composed commands | N/A: no PR commands | None. |

## Migration / Rollout

Merge gates first, publish `v0.1.0-rc.1`, verify signatures/attestations, then switch Compose and Coolify to that tag. No data migration is required.

## Open Questions

None.
