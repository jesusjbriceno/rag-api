# Environment contract

This document separates what this repository owns from what a deployment must
provide. Its purpose is portability: the stack can be instantiated in any
environment without changing code, and no hostname, domain, IP address, cloud
provider, orchestrator or ingress product is required or assumed by the
repository.

Platform choices are publication strategy, not architecture. The Compose
definition and its validator are one adapter; replacing them with another
platform changes the Compose file and the validator, never the application code.

## Layer 1 — Stack contract (owned by this repository)

What the repository guarantees, independently of where it runs:

- **Services and roles**: PostgreSQL with pgvector, a one-shot model downloader,
  a CPU-only embedding runtime, a one-shot migration runner, and the API. The
  companion import client is a separate Windows artifact.
- **Service names are part of the contract**: every service is prefixed
  (`rag-`) so no name can collide on a shared network. Internal references use
  those names.
- **Published contract**: `docs/api/Rag.Api.json` (OpenAPI 3.1). It declares no
  `servers` entry on purpose: the base URL is always environment-supplied.
- **Health semantics**: liveness checks only the process; readiness checks
  PostgreSQL and the embedding runtime. Both are anonymous; every data route
  requires a token.
- **Injected values**: image references, database credentials, JWT material and
  the content store path are all provided by the environment. Nothing is baked
  in.

## Layer 2 — Environment contract (provided by each deployment)

Every value below is supplied per deployment. None of them appears in this
repository, and a new environment is a new set of values, not a code change.

| Parameter | What the environment must provide | How to verify it |
| --- | --- | --- |
| Image references | An immutable tag or digest per application image | Pull fails on drift; the reference is not floating |
| Database credentials | Database name, role and password | Migration applies; readiness turns healthy |
| Token identity | Issuer, audience, one active signing key and its matching public validation key | The API starts; a credential exchange returns a token |
| Public API base URL | An HTTPS endpoint for clients that live outside the deployment host | A client exchanges a credential and reads a collection |
| Machine ingress authentication | Every external client authenticated at the edge, on top of the API token | An unauthenticated request is rejected before reaching the API |
| Origin exposure | Internal services (database, embedding runtime) not reachable from outside the host, and not reachable from the public internet at all | Direct access attempts fail; only the API endpoint answers |
| Internal access | For consumers running on the same host, a network where the API service name resolves | A sibling container reaches the API by service name |
| Service credential | A key id and secret issued to each consuming application | Exchange succeeds and the granted operations work |
| Content and database storage | Persistent volumes for data, model artifact and content | Data survives a redeploy |
| Backup destination | A scheduler that runs the repository backup script, plus retention and off-host storage | A restore is exercised, not assumed |

### Ingress requirements are provider-neutral

The repository states requirements, not products. Any of these satisfy the
contract: a tunnel, a reverse proxy, a load balancer, or a platform ingress. What
matters is that the endpoint terminates TLS, that external machine clients are
authenticated at the edge, and that the origin is not directly reachable.

## Client integration contract

Independent of environment, a client needs only: a base URL, a credential, and
the HTTP contract.

1. **Exchange** the credential at `POST /api/v1/auth/token` for a short-lived
   bearer token. Cache it: the exchange is rate limited per caller and the token
   is valid for 15 minutes.
2. **Use** the bearer token on every data route. Health routes are anonymous.
3. **Respect the payload limit** on ingestion: a single request body is capped at
   1 MiB, so large documents must be split by the client.
4. **Handle both success shapes** on ingestion: a synchronous completion and an
   accepted-for-processing response, the latter requiring operation polling.

Known gap at this revision: there is no collection listing endpoint. A client can
create a collection and act on it, but cannot enumerate what exists. Enumeration
is available only through the operator CLI, and only for unowned collections.

## Adapters

An **adapter** instantiates Layer 2 for a specific platform. The current one is
Coolify: it supplies the Compose deployment model, the shared network, the
prefixed-name rule and the ingress wiring. Its rules are recorded with their
sources in `compose.coolify.yaml` and `scripts/validate-coolify-compose.py`.

Adapters are replaceable. A different platform brings its own Compose file,
validator and ingress documentation. The stack contract and the client contract
above do not change.
