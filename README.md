# Private RAG API delivery

This repository deploys the RAG API, PostgreSQL with pgvector, Ollama, database migration, and model pull as one private Coolify Compose stack. The API has no public domain or published production port.

## Quick path

1. In Coolify, create a **Service Stack** from this repository and select `compose.coolify.yaml`.
2. Add the deployment secrets listed in [Coolify delivery](docs/deployment/coolify.md#secret-inventory); never upload this repository's `.env.example` as production configuration.
3. Deploy. Coolify starts PostgreSQL and Ollama, completes the idempotent migration and model-pull jobs, then starts the API.
4. From a separate client stack, use Coolify's generated full API hostname on its predefined network. Do not create a domain or publish a port for this stack.

For local-only access, copy `.env.example` to `.env` with disposable values and run:

```bash
docker compose --env-file .env -f compose.coolify.yaml -f compose.dev.yaml up --build
```

The API is then loopback-only at `http://127.0.0.1:8080`. The placeholder JWT values intentionally prevent real API startup until valid deployment-only key material is supplied.

## Operational entry points

| Need | Command or route |
| --- | --- |
| Process liveness | `GET /api/v1/health/live` |
| Dependency readiness | `GET /api/v1/health/ready` |
| Database migration | `Rag.Operator migrate` |
| Create client credentials | `Rag.Operator issue <service-client-name>` |
| Backup contract | `scripts/backup-rag.sh <backup-id> <output-directory> <content-directory>` |

Health endpoints are intentionally anonymous for orchestration. All data routes remain protected by the fallback JWT authorization policy.

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

This is the only cross-stack path. PostgreSQL and Ollama remain internal; do not target them from n8n or another client stack.

### Legacy collection ownership

For a database that predates collection ownership: deploy the preparatory migration, run `Rag.Operator issue <service-client-name>`, keep its printed `ServiceClientId`, list unowned collections, assign each deliberately, then rerun `Rag.Operator migrate` with enforcement enabled. The full command sequence is in the [legacy ownership migration](docs/deployment/coolify.md#legacy-ownership-migration) runbook.

### Health and recovery

`/live` checks only whether the API process runs. `/ready` additionally checks PostgreSQL connectivity and that Ollama lists the configured model through `GET /api/tags`; it never creates an embedding as a health-check side effect.

Hermes can schedule `scripts/backup-rag.sh`, but the scheduler owns retention, destination transfer, alerting, and restore exercises. Recovery restores the PostgreSQL custom dump and the content tar into compatible, mounted storage before reopening writers. See [backup and recovery](docs/deployment/coolify.md#backup-and-recovery-contract).

## Delivery boundaries

- Production Compose intentionally has no `ports`, `domains`, or custom `networks` declarations.
- PostgreSQL and Ollama are internal stack dependencies; external client stacks receive access only to the API when connected through Coolify's predefined network.
- The initial target is CPU-only Ollama with `qwen3-embedding:0.6b` on a 96 GB Minisforum MS-A2. Expect ingestion throughput to be CPU-bound and measure queue age before increasing concurrency.
- GPU/NVIDIA runtime, bulk retry or replay policy, and backup destinations are explicitly out of scope. A future GPU deployment must use a separate profile rather than altering this CPU stack.

Read the [Coolify delivery guide](docs/deployment/coolify.md) before the first deployment or recovery.
