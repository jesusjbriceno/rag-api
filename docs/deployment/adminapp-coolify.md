# AdminApp Coolify runbook

**Invariant: assign an FQDN only to AdminApp, and bind that exact externally supplied FQDN to a Cloudflare Access application before administrative access is enabled. Never assign a domain or a published port to the API, PostgreSQL, or llama.cpp.**

AdminApp is the backend-for-frontend (BFF) that authenticates administrators through Cloudflare Access and proxies exactly the nine existing `/api/v1/admin/*` operations to the internal API. It is the only service in `compose.coolify.yaml` with public Coolify routing. Every other service stays internal to the Docker network. This runbook documents operator procedures only; it performs no deployment and publishes nothing.

## Quick path

1. Verify the immutable AdminApp image reference and its publication evidence.
2. Supply the AdminApp configuration references in Coolify (see the [configuration table](#adminapp-configuration-references)).
3. Configure the Cloudflare Access application and its policy externally for the chosen FQDN.
4. Configure Coolify routing for `admin` only, and only after the Access boundary is ready.
5. Confirm unauthenticated traffic is stopped at the edge and that valid-but-unallowlisted identities are denied by the BFF.
6. Allow one operator-selected subject, verify access, and create the first client through a proxied operation.

## Prerequisites

| Prerequisite | Owner |
| --- | --- |
| A verified, immutable `ghcr.io/jesusjbriceno/rag-adminapp` image reference | Operator (see below) |
| Cloudflare zone control for the FQDN, and an Access application with its policy | External — Cloudflare administration |
| The RAG stack already deployed and `/api/v1/health/ready` returning `200` | Operator (see [the Coolify guide](coolify.md)) |
| Administrator identity values for the allowlist | External — identity provider / Access |
| Secret storage for every credential below | External — secret store |

All values in the "External" rows above are outside this repository. This runbook never supplies them, and this change performs none of that configuration.

## AdminApp configuration references

Set these in Coolify's deployment environment for the stack. They are references only: `compose.coolify.yaml` defines no defaults and no literal values. Never commit them, add them to `.env.example`, or paste them into this repository.

| Variable | Purpose | Source |
| --- | --- | --- |
| `RAG_ADMINAPP_IMAGE_REFERENCE` | Immutable image suffix (tag or `@sha256:...`) for `ghcr.io/jesusjbriceno/rag-adminapp` | Publication evidence |
| `CLOUDFLARE_ACCESS__ISSUER` | Issuer of Cloudflare Access JWTs | External — Cloudflare |
| `CLOUDFLARE_ACCESS__AUDIENCE` | Audience (AUD) of the Access application | External — Cloudflare |
| `CLOUDFLARE_ACCESS__KEYS__0__KEY_ID` | Access signing-key identifier | External — Cloudflare |
| `CLOUDFLARE_ACCESS__KEYS__0__PUBLIC_KEY_PEM` | Access public validation key (PEM) | External — Cloudflare |
| `ADMIN_ASSERTION__ISSUER` | Issuer claim of the BFF-issued administrator assertion | Operator-selected |
| `ADMIN_ASSERTION__AUDIENCE` | Audience claim expected by the API verifier | Must match the API verifier configuration |
| `ADMIN_ASSERTION__APP_ID` | App id bound into the assertion | Must match an `AdminAppAuth__Apps` entry |
| `ADMIN_ASSERTION__KEY_ID` | Assertion signing-key identifier | Operator-selected |
| `ADMIN_ASSERTION__PRIVATE_KEY_PEM` | Assertion private key (PEM) | Operator-generated, deployment-only secret |
| `ADMIN_APP_AUTH__APPS__0__APP_ID` | AdminApp credential registered on the API | From API-side registration |
| `ADMIN_APP_AUTH__APPS__0__KEY_ID` | AdminApp credential key identifier | From API-side registration |
| `ADMIN_APP_AUTH__APPS__0__CURRENT_SECRET` | Current HMAC secret for the machine proof | Deployment-only secret |
| `ADMIN_APP_AUTH__APPS__0__PREVIOUS_SECRET` | Previous HMAC secret, accepted only as the optional empty reference | Optional — rotation overlap only |
| `ADMIN_API__BASE_URL` | Downstream API origin; the validator requires the exact internal root origin `http://api:8080/` | Fixed internal value |
| `ALLOWED_ADMIN_SUBJECTS__0` | One allowlisted Cloudflare Access subject | External — Access identity |
| `ADMINAPP_FQDN` | Public FQDN of the AdminApp; an environment reference only, allowed only on the `admin` service | External — DNS ownership |

The BFF fails closed: startup aborts if any required Cloudflare Access, assertion, AdminApp-auth, API, or allowlist setting is missing, and requests without a valid current Access token receive `401` before any assertion or machine proof is issued. An absent or empty allowlist authorizes no subject.

### Verify the image before pinning

Use the same verification contract as the API and operator images: cosign signature, SPDX SBOM, and SLSA provenance, then confirm the manifest lists `linux/amd64` and `linux/arm64`. The exact commands and the digest-pin model are in [Image publication, verification, and rollback](coolify.md#image-publication-verification-and-rollback); substitute `ghcr.io/jesusjbriceno/rag-adminapp`.

## Cloudflare Access boundary

Cloudflare Access is the public access boundary for the AdminApp FQDN. The BFF validates the Access JWT (RS256, configured issuer, audience, and key) on every administrative request, but it does not replace the edge: the Access application must reject unauthenticated traffic before it reaches Coolify.

| Input | Where it is configured |
| --- | --- |
| Cloudflare team domain | External — Cloudflare |
| Access application AUD | External — Cloudflare |
| JWKS / signing keys | External — Cloudflare (then mirrored into the validation-key configuration above) |
| Access policy rules (who may authenticate) | External — Cloudflare |
| FQDN, DNS records, TLS certificates | External — DNS/TLS administration |
| Allowed administrator subjects | External — identity values, entered into `ALLOWED_ADMIN_SUBJECTS__0` |
| Secret storage | External — secret store |

Nothing in this repository defines these values, and no repository check validates the Cloudflare, DNS, TLS, or firewall configuration. See [What the repository checks prove](#what-the-repository-checks-prove).

## FQDN and Coolify routing

1. Choose the FQDN and configure it externally (DNS, Cloudflare Access application, policy).
2. Set `ADMINAPP_FQDN` as an environment reference in Coolify. The Compose file accepts only the `${ADMINAPP_FQDN:?required}` reference form; a literal hostname on any service is rejected by the validator.
3. In Coolify, attach the FQDN/route to the `admin` service only — and only after the Access application is live for that FQDN.
4. Confirm the API, PostgreSQL, and llama.cpp services have no domain and no published port. They remain reachable only inside the Docker network (and cross-stack via Coolify's predefined network, as documented in the [Coolify guide](coolify.md#private-cross-stack-procedure)).

## Access enablement order

1. **Before the Access application exists:** confirm unauthenticated requests to the FQDN are stopped at the edge. No administrative operation may be reachable without a valid Access token.
2. **Valid identity, no allowlist entry:** authenticate with a valid Access identity whose subject is not in `ALLOWED_ADMIN_SUBJECTS__0`. The BFF must return `403` and issue no assertion or machine proof.
3. **Allow one subject:** add one operator-selected subject to `ALLOWED_ADMIN_SUBJECTS__0`, redeploy the configuration, and verify that subject can invoke a supported administrative operation.
4. **First client creation:** create the first client credential through a proxied operation (the BFF forwards the API's existing credential-issuance operation with its own signed assertion and machine proof). Record the credential per the [client administration guide](client-administration.md).

## Health checks

Run from an operator-controlled context inside the stack network (for example `docker compose exec admin curl ...`):

```bash
curl --fail --silent --show-error http://127.0.0.1:8080/health/live
curl --fail --silent --show-error http://127.0.0.1:8080/health/ready
```

| Endpoint | Meaning |
| --- | --- |
| `GET /health/live` | `200` while the BFF process is running; no dependencies are checked |
| `GET /health/ready` | `200` only when the configured administrative API is reachable; `503` otherwise |

The Compose healthcheck uses the readiness endpoint. A `503` after deployment means the internal API is not reachable from the `admin` container; check the API service and `ADMIN_API__BASE_URL`.

## Key rotation

Rotate in this order, without logging key material at any step:

1. **Cloudflare Access validation key** — add the new public key alongside the current one (`CLOUDFLARE_ACCESS__KEYS__...`), deploy, and confirm existing sessions still authenticate; then make the new key current and remove the old one after old tokens expire.
2. **Administrator assertion key** — generate a new RSA pair outside the repository, change `ADMIN_ASSERTION__KEY_ID` and `ADMIN_ASSERTION__PRIVATE_KEY_PEM`, and deploy. The assertion is a 60-second credential, so no overlap window is required.
3. **AdminApp HMAC secret** — set the current secret as `ADMIN_APP_AUTH__APPS__0__PREVIOUS_SECRET` first, deploy the new `CURRENT_SECRET` (the API side must accept both during the overlap), verify proxied operations, then remove the previous secret in a later deploy. The empty optional reference form is the only accepted value for a non-rotating `PREVIOUS_SECRET`.

## Rollback

Rollback selects a previous verified immutable image reference. The BFF is stateless, so no data migration is involved.

1. Verify the previous `ghcr.io/jesusjbriceno/rag-adminapp` tag or digest with cosign and its attestations (same contract as the API/operator images).
2. Set `RAG_ADMINAPP_IMAGE_REFERENCE` to that previous immutable reference and redeploy the stack.
3. Confirm `GET /health/ready` returns `200` and that an allowlisted subject can complete a proxied operation.

Cloudflare Access, the FQDN, and the allowlist are unchanged by an image rollback. A pull failure fails the deployment; there is no local-build fallback.

## What the repository checks prove

The repository's CI and validation scripts prove repository invariants only:

- The Compose guard keeps the legacy five-service block byte-equivalent to its base ref, with `admin` as the only addition.
- The paired-view validator enforces the six-service set, immutable pull-only image references, the exact `ADMINAPP_FQDN` reference form, the internal-only API origin, and no published ports, host networking, custom networks, or public-routing metadata on non-admin services.

They do **not** exercise Coolify routing, Cloudflare Access, DNS resolution, TLS, or firewall behavior. Those layers are external inputs, and the operator owns verifying them at the edge before administrative access is enabled.

## Limits

- The BFF proxies exactly the nine existing `/api/v1/admin/*` operations. Other API paths and methods are not proxied.
- No SPA is served, no new API endpoint is created, and no additional deployment target is introduced.
- Client-supplied forbidden identity headers are stripped; only BFF-generated downstream credentials reach the API.
- Cloudflare team domain, application AUD, JWKS keys, policy rules, FQDN, allowed subjects, TLS/DNS choices, and secret storage are external inputs. This change performs none of them.

## Checklist

- [ ] Image reference verified with cosign and attestations, immutable
- [ ] All required configuration references set in Coolify; no literal secrets anywhere in the repository
- [ ] Cloudflare Access application and policy live for the chosen FQDN
- [ ] Unauthenticated requests to the FQDN stopped at the edge
- [ ] Valid but non-allowlisted identity receives `403` from the BFF
- [ ] One subject allowlisted; proxied operation succeeds; first client created
- [ ] `GET /health/live` and `GET /health/ready` return `200` from an operator-controlled context
- [ ] API, PostgreSQL, and llama.cpp have no domain and no published port
