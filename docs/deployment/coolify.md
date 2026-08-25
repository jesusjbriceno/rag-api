# Deploy the private RAG stack with Coolify

Deploy this Compose stack privately, then let approved client stacks reach only the API over Coolify's predefined network. PostgreSQL and llama.cpp remain stack-internal.

## Quick path

1. Create a Coolify **Service Stack** from this repository using `compose.coolify.yaml`.
2. Add the required deployment secrets in Coolify and deploy without domains or port mappings.
3. Wait for `model-download` and `migrate` to complete successfully; llama.cpp and then the API start. The API must become ready.
4. Enable **Connect to Predefined Network** on both the RAG and client service stacks. Put the Coolify-generated full API service hostname in the client stack's environment, for example `RAG_API_BASE_URL=http://rag-api-<resource-uuid>:8080`.

Coolify creates an isolated network for each stack. Its predefined-network option makes cross-stack communication possible using generated full service names; do not add a Compose `networks` section to work around that isolation.

## Secret inventory

Set these values in Coolify's deployment environment. Do not commit them, add them to `.env.example`, or paste them into docs.

| Variable | Purpose |
| --- | --- |
| `POSTGRES_DB` | RAG database name |
| `POSTGRES_USER` | RAG database role |
| `POSTGRES_PASSWORD` | PostgreSQL password |
| `JWT__ISSUER` | Token issuer |
| `JWT__AUDIENCE` | Token audience |
| `JWT__CURRENT_SIGNING_KEY__KEY_ID` | Active RSA signing key identifier |
| `JWT__CURRENT_SIGNING_KEY__PRIVATE_KEY_PEM` | Active RSA private PEM |
| `JWT__VALIDATION_KEYS__0__KEY_ID` | Active RSA validation key identifier |
| `JWT__VALIDATION_KEYS__0__PUBLIC_KEY_PEM` | Active RSA public PEM |

### JWT key rotation

1. Generate a new RSA key pair outside the repository.
2. Add the new public key to the validation-key list while retaining the current public key.
3. Deploy that validation-only configuration and verify existing tokens still authenticate.
4. Change the current signing key id and private PEM to the new pair, with its public key still present in validation keys; deploy and verify new token exchange.
5. After every old access token has expired, remove the old public validation key and deploy again.

The application validates that the current private key matches a listed public key. PEM values are deployment-only secrets.

## Embedding artifact provenance

The service accepts only this profile:

| Property | Value |
| --- | --- |
| Provider | `llama.cpp` |
| Model | `hf://Qwen/Qwen3-Embedding-0.6B-GGUF@370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf` |
| Source revision | `370f27d7550e0def9b39c1f16d3fbaa13aa67728` |
| Exact bytes | `639150592` |
| SHA-256 | `06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439` |
| Dimensions | `1024` |

`model-download` is the only acquisition step. It uses a pinned downloader image and fetches only the pinned HTTPS source, verifies size and SHA-256 before atomically publishing the GGUF, and atomically writes `/models/Qwen3-Embedding-0.6B-Q8_0.manifest`. The manifest records source URL, revision, filename, byte count, checksum, and download time.

The `llama-cpp` service uses a pinned server image, mounts `/models` read-only, and runs `--offline --model /models/Qwen3-Embedding-0.6B-Q8_0.gguf --embedding --pooling last --embd-normalize 2 --device none`. It does not download models, use a GPU runtime, expose a public port, or provide a client-facing boundary.

## Direct-cutover stop and later reindex

This release is a direct, forward-only cutover for empty RAG data. Its migration stops when any row exists in `collections` or `chunk_embeddings`. The stop intentionally leaves all existing embedding profile fields and vectors untouched and reports that a clone-and-reindex release is required for existing Ollama data.

Do not bypass the migration with direct SQL. A later release must provide a deliberate clone-and-reindex path: clone the source data into a separately profiled target, generate vectors with the target profile, validate retrieval, and only then switch clients. That work is not part of this release.

## Private cross-stack procedure

1. Keep all RAG services without domains and without host-published ports. `compose.coolify.yaml` already enforces this.
2. Deploy the RAG stack. Coolify gives the API service a generated full hostname such as `api-<resource-uuid>`; copy the actual name from Coolify rather than guessing it.
3. In the RAG service stack settings, enable **Connect to Predefined Network**.
4. In each approved client stack, enable the same option.
5. Set that client's API base URL to `http://<actual-full-api-service-name>:8080`, redeploy it, and authenticate with issued client credentials.

Do not use `postgres` or `llama-cpp` from client stacks. Those names resolve only inside the RAG stack and are intentionally not exposed as a client integration surface.

## Startup and health semantics

| Service | Gate | Meaning |
| --- | --- | --- |
| `postgres` | `pg_isready` | PostgreSQL accepts connections |
| `model-download` | HTTPS download, byte count, SHA-256, atomic publication | The immutable model artifact and manifest are available |
| `migrate` | `Rag.Operator migrate` | EF Core applies pending migrations idempotently or explicitly stops the cutover |
| `llama-cpp` | Local verified GGUF | CPU-only embedding runtime starts offline |
| `api` liveness | `/api/v1/health/live` | The API process is alive; no dependencies are checked |
| `api` readiness | `/api/v1/health/ready` | PostgreSQL is reachable and llama.cpp `GET /health` returns a valid `200` ready response |

Readiness treats llama.cpp `503` loading responses, transport failures, and malformed `200` responses as unhealthy. It does not generate an embedding or trigger model download. Both health routes are anonymous. Every collection, ingestion, operation, and retrieval route remains JWT-protected.

## Legacy ownership migration

Fresh deployments need no intervention. A database containing collections from before service-client ownership may stop at the ownership enforcement migration by design.

1. Deploy an image containing the preparatory ownership migration, not the enforcement migration.
2. Run `Rag.Operator issue <service-client-name>` and record the printed `ServiceClientId` with the generated credential.
3. Run `Rag.Operator collections list-unowned`.
4. For every deliberate assignment, run `Rag.Operator collections assign-owner <collection-id> <service-client-id>`.
5. Confirm the unowned list is empty, then deploy or rerun `Rag.Operator migrate` with the enforcement migration.

Assignments never create owners or reassign owned collections. Do not bypass the migration's deliberate stop with direct SQL.

## Backup and recovery contract

An external scheduler may run the repository script; it must provide a mounted content-volume directory and standard libpq connection variables. The script has no Docker, cloud, Dropbox, or destination-provider integration.

```bash
PGHOST=<postgres-host> PGPORT=5432 PGDATABASE=<database> PGUSER=<user> PGPASSWORD=<password> \
  scripts/backup-rag.sh <utc-backup-id> <local-output-directory> <mounted-rag-content-directory>
```

The output is atomically published as `<output>/<backup-id>/` only after verification and contains:

- `postgres.dump`: a custom-format PostgreSQL dump.
- `content.tar`: a deterministic, sorted content-volume snapshot.
- `manifest.txt` and `SHA256SUMS`: contract metadata and checksums.

Exit code `0` means a verified backup; `2` means invalid contract input or missing tool; `3` is PostgreSQL dump failure; `4` is content snapshot failure; `5` is verification failure. The scheduler owns retention, copying the completed directory to a destination, alerting, and testing restores.

For recovery, stop writers, restore `postgres.dump` with `pg_restore` into a compatible pgvector PostgreSQL instance, verify the `vector` extension, extract `content.tar` into the mounted content volume, restore API write access, and check `/api/v1/health/ready`. Recovery operators own destination retrieval, volume mounting, access permissions, and post-restore data validation.

## Out of scope

- Public domains or host-published RAG service ports.
- Custom Coolify Compose networks.
- GPU/NVIDIA runtime configuration.
- General-infrastructure model runtime or automation deployment.
- Bulk retry or replay policy for failed ingestion operations.
- Backup destinations, Dropbox integration, and provider credentials.
