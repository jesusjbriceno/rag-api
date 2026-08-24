# Deploy the private RAG stack with Coolify

Deploy this Compose stack privately, then let approved client stacks reach only the API over Coolify's predefined network. PostgreSQL and Ollama remain stack-internal.

## Quick path

1. Create a Coolify **Service Stack** from this repository using `compose.coolify.yaml`.
2. Add the required deployment secrets in Coolify and deploy without domains or port mappings.
3. Wait for `migrate` and `model-pull` to complete successfully; the `api` service then starts and must become ready.
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

`OLLAMA_MODEL` defaults to `qwen3-embedding:0.6b` in `.env.example`; keep it equal to the configured embedding model unless the application configuration changes in the same release.

### JWT key rotation

1. Generate a new RSA key pair outside the repository.
2. Add the new public key to the validation-key list while retaining the current public key.
3. Deploy that validation-only configuration and verify existing tokens still authenticate.
4. Change the current signing key id and private PEM to the new pair, with its public key still present in validation keys; deploy and verify new token exchange.
5. After every old access token has expired, remove the old public validation key and deploy again.

The application validates that the current private key matches a listed public key. PEM values are deployment-only secrets.

## Private cross-stack procedure

1. Keep all RAG services without domains and without host-published ports. `compose.coolify.yaml` already enforces this.
2. Deploy the RAG stack. Coolify gives the API service a generated full hostname such as `api-<resource-uuid>`; copy the actual name from Coolify rather than guessing it.
3. In the RAG service stack settings, enable **Connect to Predefined Network**.
4. In each approved client stack (n8n or another client), enable the same option.
5. Set that client's API base URL to `http://<actual-full-api-service-name>:8080`, redeploy it, and authenticate with issued client credentials.

Do not use `postgres` or `ollama` from client stacks. Those names resolve only inside the RAG stack and are intentionally not exposed as a client integration surface.

## Startup and health semantics

| Service | Gate | Meaning |
| --- | --- | --- |
| `postgres` | `pg_isready` | PostgreSQL accepts connections |
| `ollama` | `ollama list` | Ollama server accepts local CLI requests |
| `migrate` | `Rag.Operator migrate` | EF Core applies pending migrations idempotently |
| `model-pull` | `ollama pull qwen3-embedding:0.6b` | Required model is present in persistent Ollama storage |
| `api` liveness | `/api/v1/health/live` | The API process is alive; no dependencies are checked |
| `api` readiness | `/api/v1/health/ready` | PostgreSQL is reachable and Ollama lists the configured model |

Readiness calls Ollama `GET /api/tags`; it does not generate an embedding or load a model merely to report health. Both health routes are anonymous. Every collection, ingestion, operation, and retrieval route remains JWT-protected.

## Legacy ownership migration

Fresh deployments need no intervention. A database containing collections from before service-client ownership may stop at the enforcement migration by design.

1. Deploy an image containing the preparatory ownership migration, not the enforcement migration.
2. Run `Rag.Operator issue <service-client-name>` and record the printed `ServiceClientId` with the generated credential.
3. Run `Rag.Operator collections list-unowned`.
4. For every deliberate assignment, run `Rag.Operator collections assign-owner <collection-id> <service-client-id>`.
5. Confirm the unowned list is empty, then deploy or rerun `Rag.Operator migrate` with the enforcement migration.

Assignments never create owners or reassign owned collections. Do not bypass the migration's deliberate stop with direct SQL.

## CPU sizing and image pinning

The initial host is a 96 GB Minisforum MS-A2 and runs Ollama on CPU only. `qwen3-embedding:0.6b` is intentionally small, but ingestion throughput is CPU-bound; measure queue age and CPU saturation before raising concurrency or choosing a larger model.

The tracked image tags match this codebase's .NET 10 and pgvector/PostgreSQL 16 requirements. Before production deployment, resolve each image tag to an approved immutable digest in Coolify or the deployment registry. Update and review that pin as a normal release change. GPU/NVIDIA runtime is out of scope for this stack; any future GPU deployment must be introduced as a separate profile rather than changing this CPU baseline.

## Backup and recovery contract

Hermes may schedule the repository script; it must provide a mounted content-volume directory and standard libpq connection variables. The script has no Docker, cloud, Dropbox, or destination-provider integration.

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
- Bulk retry or replay policy for failed ingestion operations.
- Backup destinations, Dropbox integration, and provider credentials.
