# Proposal: Deployment Package Publication

## Intent

Close the foundation feature at a versioned pre-release, introduce CI/CD that builds and publishes Docker images to GHCR, and remove image-building responsibility from Compose. Defers Dockerfile/Compose architecture rethinking until the package-generation model is validated.

## Scope

### In Scope
- GitHub Actions workflows: PR gate, develop pipeline (build/test/SonarQube/image publish), release pipeline (tag-triggered)
- Docker image build, Trivy vulnerability scan, cosign signing, GHCR publication
- Compose migration from `build:` to `image:` referencing immutable GHCR tags
- Foundation closure at `v0.1.0-rc.1` (pre-release)
- Version metadata injection into images and application

### Out of Scope
- Dockerfile restructuring or multi-project image split
- n8n integration (rejected — overkill for CI/CD; GitHub Actions suffices)
- NuGet package publishing (rejected — service, not library)
- SonarQube PR/branch analysis (Community Edition limitation)
- Compose signature verification at runtime
- Production deployment automation beyond image pinning

## Capabilities

### New Capabilities
- `ci-cd-pipeline`: GitHub Actions workflows for PR validation, develop integration, and release publication
- `package-publication`: Docker image build, vulnerability scanning (Trivy), signing (cosign), and GHCR publishing with version tags
- `compose-runtime`: Compose consuming pre-built GHCR images via immutable version tags instead of local builds

### Modified Capabilities
None — no existing specs to modify.

## Approach

Three GitHub Actions workflows:

| Workflow | Trigger | Key behavior |
|----------|---------|--------------|
| `ci-pr.yml` | `pull_request` to `develop` | Build + test only. No SonarQube, no image publish. |
| `ci-develop.yml` | `push` to `develop` | Build, test, Coverlet coverage, SonarQube Community analysis, Docker build, Trivy scan (block on HIGH/CRITICAL), cosign sign, push to GHCR as `develop-{sha}`. Auto-promote to pre-release channel. |
| `ci-release.yml` | semver tag `v*` push | Build, test, Docker build, Trivy scan, cosign sign, push to GHCR as `{version}`, create GitHub Release. |

**Version/channel semantics**:
- Foundation closes at `v0.1.0-rc.1` (pre-release, not stable)
- Develop builds auto-promote to pre-release channel with `develop-{sha}` tags
- Release tags produce immutable `{version}` tags
- Production Compose pins `RAG_API_IMAGE_TAG` to a specific version (never `latest`)

**Security gates**: Trivy blocks publication on HIGH or CRITICAL findings. cosign keyless OIDC signing proves provenance.

**Compose changes**: Replace `build:` sections in `compose.coolify.yaml` with `image: ghcr.io/jesusjbriceno/rag-api:${RAG_API_IMAGE_TAG}`. Dev override (`compose.dev.yaml`) retains local build for rapid iteration.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `.github/workflows/` | New | Three workflow files (PR, develop, release) |
| `compose.coolify.yaml` | Modified | Replace `build:` with `image:` from GHCR |
| `compose.dev.yaml` | Unchanged | Retains local build for dev iteration |
| `Dockerfile` | Unchanged | Kept as-is; rethinking deferred |
| `Directory.Build.props` | New/Modified | Version metadata injection |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| GitHub Actions minutes exhaustion | Medium | Cache dependencies, skip unnecessary jobs, monitor usage |
| GHCR storage costs | Low | Container retention policies, prune old images |
| Trivy false positives blocking publish | Medium | Configure ignore rules, review before failing |
| Pre-release tag confusion | Low | Clear documentation: `-rc.N` = pre-release, not production-ready |

## Rollback Plan

1. Revert `compose.coolify.yaml` to `build:` sections (git revert the Compose change)
2. Disable workflows via GitHub Actions UI or delete workflow files
3. Existing Dockerfile remains functional for local builds
4. GHCR images are immutable — pin to last known good tag

## Dependencies

- GitHub Actions (free tier: 2,000 min/month)
- GHCR (free tier: 500 MB storage)
- SonarQube Community Edition (self-hosted or SonarCloud)
- cosign + Sigstore OIDC (GitHub Actions integration)
- Trivy (GitHub Actions action)

## Success Criteria

- [ ] PR workflow blocks merge on test failure
- [ ] Develop workflow publishes signed, scanned images to GHCR on every push
- [ ] Release workflow creates GitHub Release with immutable image tags on semver tag push
- [ ] Production Compose pulls immutable version tag (never `latest`)
- [ ] Trivy blocks publication on HIGH/CRITICAL vulnerabilities
- [ ] Foundation merged and tagged `v0.1.0-rc.1`
- [ ] No `build:` directives in production Compose file
