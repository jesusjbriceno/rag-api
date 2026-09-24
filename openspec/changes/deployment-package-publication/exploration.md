# Exploration: Deployment and Package Publication Strategy

## Purpose

Define a deployment and publication strategy for the RAG API before revisiting Compose/Dockerfile. Close the foundation feature at a versioned release point. Design CI/CD so SonarQube Community analysis runs only from `develop`, CI builds a distributable package and publishes it to GitHub Packages, and package integrity/security validation is included. Evaluate whether an n8n workflow is justified. Remove image-building responsibility from Compose.

## Current State

### Repository topology

- **Remote**: `https://github.com/jesusjbriceno/rag-api` (private).
- **Branches**: `main` and `develop` both at `f9aa35d inicializacion`. `feature/rag-foundation` has 9 additional commits (the complete foundation implementation).
- **Tags**: None.
- **GitHub Actions workflows**: None.
- **SonarQube configuration**: None.
- **Version metadata in csproj**: None. No `Version`, `AssemblyVersion`, `FileVersion`, or `InformationalVersion` properties.

### Package shape

- **Solution**: `Rag.sln` with 7 projects:
  - `src/Rag.Domain` - core entities and invariants.
  - `src/Rag.Application` - use case orchestration.
  - `src/Rag.Infrastructure` - EF Core, PostgreSQL/pgvector, JWT, embedding provider, content store.
  - `src/Rag.Api` - ASP.NET Core HTTP entry point.
  - `src/Rag.Operator` - CLI tool for migrations and credential management.
  - `tests/Rag.UnitTests` - xunit unit tests.
  - `tests/Rag.IntegrationTests` - xunit integration tests with Testcontainers.PostgreSql.
- **Target framework**: `net10.0` (.NET 10).
- **Two deployable artifacts**: `Rag.Api` (web API) and `Rag.Operator` (CLI/migration tool).

### Dockerfile

Multi-stage build with three targets:
- `build` - restores and publishes both `Rag.Api` and `Rag.Operator`.
- `api` - runtime image for the API (port 8080, non-root `rag` user).
- `operator` - runtime image for the Operator CLI.

### Compose files

- `compose.coolify.yaml` - production Coolify stack. **Builds images from the Dockerfile** (`build: context: ., dockerfile: Dockerfile, target: api|operator`). This is the problem to solve.
- `compose.dev.yaml` - local dev override (port mapping only).

### Testing

- **Unit tests**: 11 test classes in `Rag.UnitTests`.
- **Integration tests**: 8 test classes in `Rag.IntegrationTests` using Testcontainers.PostgreSql.
- **Coverage tooling**: None configured. No Coverlet, no report generation.
- **Test runner**: `dotnet test` (xunit).

### Deployment

- **Target**: Private Coolify Compose stack.
- **Embedding runtime**: llama.cpp with a pinned, verified Qwen GGUF model.
- **Database**: PostgreSQL 16 with pgvector.
- **Authentication**: RS256 JWT with key rotation support.
- **Health checks**: `/api/v1/health/live` and `/api/v1/health/ready`.

### Foundation feature status

The `feature/rag-foundation` branch contains a complete, working implementation:
- TXT ingestion pipeline (upload -> parse -> chunk -> embed -> index).
- Semantic retrieval with similarity search.
- Service client credential management (issue, rotate, revoke).
- Collection ownership enforcement.
- Database migrations (7 migrations).
- Health checks.
- Backup script.

The foundation is ready to be merged and closed at a versioned release.

## Affected Areas

- `Dockerfile` - currently builds images; will remain the image definition but CI will build and publish images, not Compose.
- `compose.coolify.yaml` - currently builds images; must be changed to pull pre-built images from GitHub Packages (GHCR).
- `.github/workflows/` - does not exist; must be created for CI/CD.
- `Directory.Build.props` or individual csproj files - need version metadata injection.
- `openspec/config.yaml` - testing section reports "unavailable"; needs update to reflect actual test infrastructure.
- `global.json` - pins .NET SDK 10.0.111; CI must use compatible SDK.

## Approaches

### 1. GitHub Actions CI/CD with Docker image publishing to GHCR

**Description**: Use GitHub Actions for all CI/CD. Build Docker images in CI, push to GitHub Container Registry (GHCR). Compose pulls images from GHCR instead of building. SonarQube Community analysis runs only on `develop`. Image signing with cosign, vulnerability scanning with Trivy.

**CI/CD pipeline structure**:

- **PR workflow** (on `pull_request` to `develop`):
  - Build and test (unit + integration).
  - No SonarQube analysis (PR analysis requires Developer Edition).
  - No image build or publish.

- **Develop workflow** (on `push` to `develop`):
  - Build and test (unit + integration).
  - Generate coverage report with Coverlet.
  - Run SonarQube Community analysis (begin, analyze, end).
  - Build Docker images (api, operator).
  - Scan images with Trivy (fail on CRITICAL vulnerabilities).
  - Sign images with cosign (keyless, OIDC).
  - Push to GHCR with tags: `develop`, `develop-{sha}`.

- **Release workflow** (on `push` of semver tag `v*` or manual trigger):
  - Build and test.
  - Generate coverage report.
  - Build Docker images.
  - Scan with Trivy.
  - Sign with cosign.
  - Push to GHCR with tags: `{version}`, `latest`.
  - Create GitHub Release with release notes.

**Compose changes**:
- Replace `build:` sections with `image: ghcr.io/${{ github.repository }}/rag-api:{tag}`.
- Use environment variable for image tag (e.g., `RAG_API_IMAGE_TAG`).
- Default to `develop` tag for dev, `latest` or specific version for production.

**Versioning**:
- SemVer with Git tags (e.g., `v0.1.0`, `v1.0.0`).
- Inject version into Docker images via build args (`VERSION`, `GIT_COMMIT`, `BUILD_DATE`).
- Inject version into application via `InformationalVersion` or environment variable.

**Pros**:
- Native GitHub integration; no external CI service needed.
- GHCR is free for public repos, low cost for private repos.
- cosign and Trivy are industry standards, well-maintained.
- SonarQube Community is free, self-hosted or cloud.
- Clear separation: CI builds and publishes, Compose only pulls and runs.
- Full audit trail: GitHub Actions logs, GHCR image metadata, cosign signatures.

**Cons**:
- Requires GitHub Actions minutes (free tier: 2,000 min/month for private repos).
- Requires GHCR storage (free tier: 500 MB for private repos).
- SonarQube Community has limitations vs. Developer Edition (no branch analysis, no PR analysis).
- cosign keyless signing requires OIDC provider (GitHub Actions supports this).

**Effort**: Medium-High. Requires writing 3 workflows, configuring SonarQube, setting up GHCR, updating Compose, adding versioning.

### 2. GitHub Actions CI/CD with NuGet package publishing (rejected)

**Description**: Publish NuGet packages to GitHub Packages instead of Docker images.

**Why rejected**: This is a service, not a library. NuGet packages are for distributing reusable .NET libraries. The RAG API is a deployable application with runtime dependencies (PostgreSQL, llama.cpp). Docker images are the correct distribution mechanism. NuGet packages would require consumers to build their own runtime, which defeats the purpose of a private, self-contained service.

**Effort**: N/A (rejected).

### 3. Hybrid: GitHub Actions for CI/CD + n8n for post-deployment automation

**Description**: Use GitHub Actions for CI/CD (build, test, publish). Use n8n for post-deployment automation (notifications, backup triggers, health check monitoring).

**n8n evaluation**:
- **Strengths**: Visual workflow editor, 300+ integrations, self-hostable, good for event-driven automation.
- **Weaknesses**: Overkill for simple notifications (GitHub Actions can send Slack/email). Adds operational complexity (another service to maintain). Not a CI/CD tool; cannot replace GitHub Actions for build/test/publish.
- **Use cases where n8n is justified**:
  - Complex multi-step workflows with conditional logic and external integrations.
  - Event-driven automation triggered by webhooks from multiple sources.
  - Non-technical users need to modify workflows.
- **Use cases where n8n is NOT justified**:
  - Simple notifications (use GitHub Actions notifications or Slack webhook).
  - Backup triggers (use cron + script, or external scheduler).
  - Health check monitoring (use UptimeRobot, Healthchecks.io, or custom script).

**Recommendation**: Do NOT use n8n for this project. GitHub Actions is sufficient for CI/CD. Post-deployment automation (if needed) can be handled by:
- GitHub Actions notifications (Slack, email).
- External scheduler for backups (cron job on a VPS, or Coolify's built-in scheduler).
- Coolify's deployment hooks (webhook after deployment).

**Effort**: Medium (GitHub Actions only) vs. High (GitHub Actions + n8n).

### 4. Alternative CI/CD: GitLab CI, CircleCI, or Jenkins (rejected)

**Why rejected**: The code is on GitHub. GitHub Actions is the natural choice - native integration, no external service, free tier is sufficient. GitLab CI would require migrating to GitLab. CircleCI and Jenkins add external dependencies and complexity without clear benefit.

**Effort**: N/A (rejected).

## Recommendation

**Recommended approach**: Option 1 (GitHub Actions CI/CD with Docker image publishing to GHCR) with the following specifics:

### CI/CD pipeline

1. **PR workflow** (`ci-pr.yml`):
   - Trigger: `pull_request` to `develop`.
   - Jobs: build, test (unit + integration).
   - No SonarQube analysis (PR analysis requires Developer Edition).
   - No image build or publish.

2. **Develop workflow** (`ci-develop.yml`):
   - Trigger: `push` to `develop`.
   - Jobs:
     - `build-test`: build, test, generate coverage with Coverlet.
     - `sonarqube`: run SonarQube Community analysis (requires self-hosted SonarQube or SonarCloud).
     - `docker`: build images, scan with Trivy, sign with cosign, push to GHCR with tags `develop` and `develop-{sha}`.

3. **Release workflow** (`ci-release.yml`):
   - Trigger: `push` of semver tag `v*` or manual trigger.
   - Jobs:
     - `build-test`: build, test, generate coverage.
     - `docker`: build images, scan with Trivy, sign with cosign, push to GHCR with tags `{version}`, `latest`.
     - `release`: create GitHub Release with release notes.

### SonarQube Community strategy

- **Self-hosted SonarQube Community** or **SonarCloud** (free for public repos, paid for private).
- Analysis runs ONLY on `develop` branch (per requirement).
- Coverage report generated by Coverlet in `build-test` job, passed to SonarQube.
- Quality gates: fail build if coverage drops below threshold or new bugs/vulnerabilities introduced.

### Image integrity and security validation

**Tools**:
- **cosign** (Sigstore): image signing with keyless OIDC (GitHub Actions). Proves image provenance.
- **Trivy**: vulnerability scanning. Fail on CRITICAL vulnerabilities.
- **syft**: SBOM generation (optional, for supply chain transparency).

**Validation gates**:
- Trivy scan MUST pass before pushing to GHCR.
- cosign signature MUST be verifiable before Compose pulls the image.
- Compose SHOULD verify image signature before starting services (optional, adds complexity).

### Versioning strategy

- **SemVer** with Git tags (e.g., `v0.1.0`, `v1.0.0`).
- **Foundation closure**: merge `feature/rag-foundation` to `develop`, then tag `v0.1.0` as the first foundation release.
- **Docker image tags**:
  - `develop` builds: `develop`, `develop-{sha}`.
  - Release builds: `{version}`, `latest`.
- **Application version**: inject via `InformationalVersion` or environment variable (`VERSION`).

### Compose changes

- Replace `build:` sections with `image: ghcr.io/jesusjbriceno/rag-api:{tag}`.
- Use environment variable for image tag: `RAG_API_IMAGE_TAG` (default: `develop`).
- Production deployment: set `RAG_API_IMAGE_TAG=latest` or specific version.
- Local development: use `compose.dev.yaml` override with `build:` for rapid iteration.

### Foundation closure procedure

1. Merge `feature/rag-foundation` to `develop` via PR.
2. Run develop workflow (build, test, SonarQube, Docker build/push).
3. Verify images in GHCR.
4. Tag `develop` as `v0.1.0`.
5. Run release workflow (build, test, Docker build/push with version tags, GitHub Release).
6. Update `compose.coolify.yaml` to pull from GHCR.
7. Deploy to Coolify using `RAG_API_IMAGE_TAG=v0.1.0`.

## Risks

1. **GitHub Actions minutes**: Private repos get 2,000 min/month free. Heavy CI usage may exceed this. **Mitigation**: Optimize workflows (cache dependencies, skip unnecessary jobs). Monitor usage in GitHub settings.

2. **GHCR storage**: Private repos get 500 MB free. Docker images are large (~200-500 MB each). **Mitigation**: Delete old images periodically. Use GitHub's container retention policies.

3. **SonarQube Community limitations**: No branch or PR analysis. Only `develop` can be analyzed. **Mitigation**: Accept this limitation. Use SonarCloud if PR analysis is critical (paid for private repos).

4. **cosign keyless signing**: Requires OIDC provider. GitHub Actions supports this, but it adds complexity. **Mitigation**: Use cosign's GitHub Actions integration. Document the signing process.

5. **Trivy false positives**: May flag vulnerabilities that are not exploitable in this context. **Mitigation**: Configure Trivy to ignore specific CVEs or severities. Review scan results before failing the build.

6. **Foundation merge conflicts**: `feature/rag-foundation` may have conflicts with `develop` if other work happens. **Mitigation**: Merge `develop` into `feature/rag-foundation` before closing. Resolve conflicts early.

7. **Compose image tag management**: Operators must remember to update `RAG_API_IMAGE_TAG` when deploying. **Mitigation**: Document the deployment procedure. Use `latest` for production (with caution).

## Ready for Proposal

**Yes**. The exploration is complete. The orchestrator should inform the user that:

1. **GitHub Actions is the recommended CI/CD platform** - native GitHub integration, free tier sufficient, no external dependencies.
2. **Docker images are the correct package format** - this is a service, not a library. Publish to GHCR.
3. **n8n is NOT justified** - GitHub Actions handles CI/CD. Post-deployment automation can use GitHub notifications or external schedulers.
4. **SonarQube Community analysis runs only on `develop`** - per requirement. PR analysis requires Developer Edition or SonarCloud.
5. **Image integrity validation**: cosign for signing, Trivy for vulnerability scanning. Both are industry standards.
6. **Foundation closure**: merge `feature/rag-foundation` to `develop`, tag `v0.1.0`, update Compose to pull from GHCR.
7. **Next step**: create a proposal (`sdd-propose`) that formalizes the CI/CD pipeline structure, versioning strategy, and Compose changes.
