# Private RAG API delivery

This repository deploys the RAG API, PostgreSQL with pgvector, a private CPU-only llama.cpp embedding runtime, database migration, and verified model download as one private Coolify Compose stack. The API has no public domain or published production port.

## Quick path

1. In Coolify, create a **Service Stack** from this repository and select `compose.coolify.yaml`.
2. Add the deployment secrets listed in [Coolify delivery](docs/deployment/coolify.md#secret-inventory) and pin `RAG_API_IMAGE_TAG` to a verified published version; never upload this repository's `.env.example` as production configuration.
3. Deploy. Coolify starts PostgreSQL, downloads and verifies the immutable Qwen GGUF, applies the idempotent migration, then starts llama.cpp and the API.
4. From a separate client stack, use Coolify's generated full API hostname on its predefined network. Do not create a domain or publish a port for this stack.

For local-only access, copy `.env.example` to `.env` with disposable values and run:

```bash
docker compose --env-file .env -f compose.coolify.yaml -f compose.dev.yaml up --build
```

The API is then loopback-only at `http://127.0.0.1:8080`. The placeholder JWT values intentionally prevent real API startup until valid deployment-only key material is supplied.

## Embedding runtime

The direct-cutover profile is fixed for this build. There is no Ollama compatibility profile or fallback.

| Property | Value |
| --- | --- |
| Provider | `llama.cpp` |
| Model | `hf://Qwen/Qwen3-Embedding-0.6B-GGUF@370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf` |
| Artifact SHA-256 | `06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439` |
| Dimensions | `1024` |

The one-shot downloader fetches the pinned HTTPS artifact, verifies `639150592` bytes and its SHA-256, atomically publishes the GGUF, and writes a provenance manifest. llama.cpp mounts that storage read-only and starts offline with local `--model`, `--embedding`, `--pooling last`, `--embd-normalize 2`, and `--device none` options.

## Direct-cutover stop

This is a forward-only empty-data cutover. The migration stops before the runtime change if **any collection or chunk embedding exists**. It does not rewrite embedding profile fields or vectors. Existing Ollama data requires a later, explicit clone-and-reindex release; do not bypass this stop with direct SQL.

## Operational entry points

| Need | Command or route |
| --- | --- |
| Process liveness | `GET /api/v1/health/live` |
| Dependency readiness | `GET /api/v1/health/ready` |
| Database migration | `Rag.Operator migrate` |
| Create client credentials | `Rag.Operator issue <service-client-name>` |
| Backup contract | `scripts/backup-rag.sh <backup-id> <output-directory> <content-directory>` |

Health endpoints are intentionally anonymous for orchestration. All data routes remain protected by the fallback JWT authorization policy.

## Image publication and verification

Three least-privilege GitHub Actions workflows publish only verified images to GHCR:

| Workflow | Trigger | Result |
| --- | --- | --- |
| `ci-pr.yml` | Pull request to `develop` | Build + test gate only; no publish, no SonarQube. |
| `ci-develop.yml` | Push to `develop` | Build/test/SonarQube, then publishes `develop-<sha>` pre-release images. |
| `ci-release.yml` | Semver tag `v*` push | Build/test, then publishes immutable `vX.Y.Z` images and creates a GitHub Release (`-rc.N` tags are pre-releases). |

Every image is Trivy-scanned (HIGH/CRITICAL blocks publication), keyless-signed with cosign, and carries a SPDX SBOM plus SLSA provenance attestation. Final tags are created only after signing and verification, and an existing tag is never moved to a different digest.

Verify a release image before deploying it:

```bash
IDENTITY="https://github.com/jesusjbriceno/rag-api/.github/workflows/ci-release.yml@refs/tags/v0.1.0-rc.1"
IMAGE="ghcr.io/jesusjbriceno/rag-api:v0.1.0-rc.1"

cosign verify \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity "$IDENTITY" "$IMAGE"

cosign verify-attestation --type spdxjson \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity "$IDENTITY" "$IMAGE"
```

For `develop-<sha>` images, use `--certificate-identity "https://github.com/jesusjbriceno/rag-api/.github/workflows/ci-develop.yml@refs/heads/develop"` against the matching tag.

## Pin and roll back

Production Compose pulls `ghcr.io/jesusjbriceno/rag-api` and `rag-operator` at one shared `RAG_API_IMAGE_TAG` with `pull_policy: always`. Pin it to an exact published version (for example `v0.1.0-rc.1`), never `latest` or a floating channel.

To roll back, set `RAG_API_IMAGE_TAG` to the previous verified semver tag and redeploy: the stack re-pulls that immutable digest, and a pull failure never falls back to a local build. The full operator contract is in [the deployment guide](docs/deployment/coolify.md#image-publication-verification-and-rollback).

## Deployment checklist

### Secret inventory

Set these values only in Coolify's deployment environment. The repository never contains actual PEMs or production passwords.

| Variable group | Required values |
| --- | --- |
| PostgreSQL | `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` |
| JWT identity | `JWT__ISSUER`, `JWT__AUDIENCE` |
| Current signer | `JWT__CURRENT_SIGNING_KEY__KEY_ID`, `JWT__CURRENT_SIGNING_KEY__PRIVATE_KEY_PEM` |
| Current validator | `JWT__VALIDATION_KEYS__0__KEY_ID`, `JWT__VALIDATION_KEYS__0__PUBLIC_KEY_PEM` |

Rotate keys by first deploying the new public validation key alongside the old one, then making its private key current, and finally removing the old public key after the 15-minute token lifetime. The detailed sequence is in the [deployment guide](docs/deployment/coolify.md#jwt-key-rotation).

### Private client connection

1. Deploy the RAG Service Stack with no domains and no port mappings.
2. Enable **Connect to Predefined Network** on both the RAG stack and the approved client stack.
3. Copy Coolify's generated full API service name, such as `api-<resource-uuid>`.
4. Configure the client with `RAG_API_BASE_URL=http://<actual-full-api-service-name>:8080` and redeploy it.

This is the only cross-stack path. PostgreSQL and llama.cpp remain internal; do not target them from n8n or another client stack.

### Health and recovery

`/live` checks only whether the API process runs. `/ready` additionally checks PostgreSQL connectivity and llama.cpp `GET /health`. `200` with a valid ready payload is healthy; `503`, transport failures, and malformed responses are unhealthy. Readiness never generates an embedding.

An external scheduler can run `scripts/backup-rag.sh`, but the scheduler owns retention, destination transfer, alerting, and restore exercises. Recovery restores the PostgreSQL custom dump and the content tar into compatible, mounted storage before reopening writers. See [backup and recovery](docs/deployment/coolify.md#backup-and-recovery-contract).

## Delivery boundaries

- Production Compose intentionally has no `ports`, `domains`, or custom `networks` declarations.
- PostgreSQL and llama.cpp are internal stack dependencies; external client stacks receive access only to the API when connected through Coolify's predefined network.
- The runtime is CPU-only and uses a locally mounted, verified GGUF. GPU/NVIDIA runtime configuration is not part of this stack.
- General-infrastructure model runtimes and automation remain outside this RAG delivery boundary.

Read the [Coolify delivery guide](docs/deployment/coolify.md) before the first deployment or recovery.
