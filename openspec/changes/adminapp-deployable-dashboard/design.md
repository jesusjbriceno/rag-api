# Design: Coolify-Deployed AdminApp BFF

## Outcome

Preserve the implemented Unit 1 host shell, complete the AdminApp BFF and image, and add exactly one `admin` service to the existing Coolify Compose stack. Coolify may route an operator-supplied FQDN only to AdminApp; Cloudflare Access is the external access boundary for that FQDN. `api`, `postgres`, and `llama-cpp` remain reachable only on the Compose default network and receive no domains or host-published ports.

The AdminApp image is pull-only and immutable. Its GHCR package, `ghcr.io/jesusjbriceno/rag-adminapp`, is public so Coolify can pull it without registry credentials; package visibility does not weaken the runtime boundary. The deployed `admin` service remains private by topology: it publishes no host port, and its only external path is the operator-configured Coolify route protected by Cloudflare Access. Every image reference, FQDN, key, secret, audience, subject, and external Cloudflare value remains an environment reference or operator decision; this design supplies no operational value.

Dokploy is not a deployment target and no Dokploy manifest, validator, test, or runbook is created. This design does not deploy, tag, publish manually, commit, push, enable RDD, add an SPA, or change an existing API operation.

## Pre-Apply Design Closure

The following decisions are final for PR 1 and downstream slices:

| Gate | Locked decision |
| --- | --- |
| BFF signer ownership | The signer is `src/Rag.AdminApp.Host/Proxy/AdminProxyRequestSigner.cs`. It is host-local and does not occupy the future SPA/application namespace. |
| Compose validator migration | Preserve every legacy five-service invariant and diagnostic while atomically extending the accepted topology to exactly six services: `postgres`, `model-download`, `llama-cpp`, `migrate`, `api`, `admin`. Only `admin` may carry FQDN/domain configuration. |
| Package visibility vs. runtime exposure | Publish `ghcr.io/jesusjbriceno/rag-adminapp` as a public GHCR package. Keep the deployed service without published ports and permit administrative ingress only through Cloudflare Access. |
| FQDN injection | The sole accepted source form is `ADMINAPP_FQDN: ${ADMINAPP_FQDN:?required}` on `admin`. No literal, default, alternate variable, generated value, or occurrence on another service is allowed. |

These decisions resolve the design blockers recorded in `tasks.md`; they do not authorise implementation, publication, deployment, or external configuration.

## Current Baseline and Preservation Rule

Unit 1 is complete and remains authoritative:

- `Rag.Infrastructure.AdminAuthenticationContract` owns the six downstream header names and 1 MiB authenticated-body limit.
- `Rag.Api.AdminAuthenticationDefaults` aliases that shared contract without changing API wire behaviour.
- `src/Rag.AdminApp.Host/` is a namespaced ASP.NET Core composition root with startup-validated `CloudflareAccess`, `AdminAssertion`, `AdminAppAuth`, and `AdminApi` sections.
- `AllowedAdminSubjects` binds as `string[]`, rejects blank configured entries, and becomes an ordinal, case-sensitive set. Missing or empty configuration authorises nobody.
- The host project is already in `Rag.sln`, and its focused Unit 1 tests are already implemented.

Regenerating this design does not reopen or rewrite Unit 1. Later work adds registrations and endpoint mappings through the existing `AddAdminAppHost` and `MapAdminAppHost` extension points.

## Architecture at a Glance

```text
Internet
  |
  v
Cloudflare Access application and policy          external operator-owned boundary
  |  Cf-Access-Jwt-Assertion
  v
operator-supplied AdminApp FQDN
  |
  v
Coolify routing -> admin:8080                     only routed Compose service
                  |  validate Access JWT + allowlist
                  |  issue 60-second RS256 assertion
                  |  create HMAC-SHA256 machine proof
                  v
                  api:8080 /api/v1/admin/*         default Docker network only
                    |                 |
                    v                 v
                 postgres         llama-cpp         default Docker network only
```

There are two independent access controls:

1. Cloudflare Access decides whether traffic reaches the AdminApp FQDN.
2. The BFF validates the Access JWT and its own exact subject allowlist before it creates downstream credentials.

The repository can validate the source-controlled Compose topology. It cannot inspect or prove an external Cloudflare policy or Coolify dashboard assignment; the runbook therefore makes external binding and verification explicit rollout gates.

## Decisions

### 1. Keep the explicit, composable ASP.NET Core host

The implemented `Rag.AdminApp.Host.Program` remains a composition root only:

1. create the builder;
2. call `AddAdminAppHost(configuration)`;
3. build;
4. call `MapAdminAppHost()`;
5. run.

Registration, authentication, proxy, and health behaviour live in focused extension classes. The later `admin-application` change can add static files and historical-import endpoints without replacing this pipeline.

No fallback authorisation policy, catch-all proxy, YARP route table, SPA middleware, CORS policy, or new API operation is introduced.

### 2. Preserve the shared downstream authentication wire contract

The BFF uses the implemented `AdminAuthenticationContract` members:

- `X-Admin-App-Id`
- `X-Admin-Key-Id`
- `X-Admin-Timestamp`
- `X-Admin-Signature`
- `X-Admin-Assertion`
- `Idempotency-Key`
- `MaxBodyBytes = 1_048_576`

`AdminAuthenticationDefaults` remains an API compatibility alias. The BFF directly reuses `AdminMachineProof`, `AdminMachineProofVerifier.Canonicalize`, `AdminAppAuthOptions`, and `AdminIdentityHeaderPolicy`; it does not create a second wire contract or canonicalisation algorithm.

### 3. Restrict proxying by construction

Register a typed `HttpClient` whose base address comes from validated `AdminApi:BaseUrl`. `AdminApiClient` accepts a closed `AdminProxyOperation`, not an arbitrary destination URI.

Map exactly these method/template pairs under `/api/v1/admin`:

| Operation | Method | Route |
| --- | --- | --- |
| Create client | POST | `/clients` |
| List clients | GET | `/clients` |
| Get client | GET | `/clients/{clientId:guid}` |
| Issue credential | POST | `/clients/{clientId:guid}/credentials` |
| List credentials | GET | `/clients/{clientId:guid}/credentials` |
| Get credential | GET | `/credentials/{credentialId:guid}` |
| Rotate credential | POST | `/credentials/{credentialId:guid}/rotate` |
| Revoke credential | POST | `/credentials/{credentialId:guid}/revoke` |
| List audit | GET | `/audit` |

Unsupported paths are unmapped, unsupported methods never invoke the client, and GUID constraints reject malformed identifiers before proxying.

The client buffers at most `MaxBodyBytes`, preserves exact body bytes and content type, resolves one non-empty idempotency key, forwards `If-Match` only for rotate/revoke, and copies only the semantic request headers `Accept`, `Content-Type`, `Idempotency-Key`, and applicable `If-Match`. It returns the downstream status/body and only `Content-Type`, `Location`, `ETag`, and `Retry-After`. Connectivity failures become a generic 502; cancellation remains cancellation.

### 4. Authenticate and authorise before issuing downstream credentials

An endpoint filter applies only to the nine admin endpoints. For each request it:

1. requires exactly one non-empty `Cf-Access-Jwt-Assertion` value;
2. calls the existing RS256-only `CloudflareAccessValidator`;
3. reads the validated opaque `sub`;
4. compares it ordinally and case-sensitively with the implemented allowlist set;
5. invokes the proxy only after authentication and authorisation succeed.

Missing, blank, multiple, invalid, or expired tokens return 401. A valid non-member returns 403. Neither path issues an assertion, calculates a proof, or contacts the API. Health endpoints remain anonymous.

Logs may record a reason category and route template, but not tokens, subjects, assertions, HMACs, private keys, bodies, or secret-bearing configuration.

### 5. Generate proof material from the API verifier's exact inputs

Use one captured `DateTimeOffset.UtcNow` for assertion issuance and proof timestamp. `AdminProxyRequestSigner` is owned exclusively by the BFF host at `src/Rag.AdminApp.Host/Proxy/AdminProxyRequestSigner.cs`. This path and type name are locked for this change: the signer does not live under `src/Rag.AdminApp/`, and the later SPA/application work must not replace or duplicate it. This host-local ownership prevents collision with future `Rag.AdminApp` application services.

For each authorised outgoing request it:

1. issues the existing 60-second RS256 assertion;
2. selects the one `AdminAppAuth:Apps` entry matching `AdminAssertion:AppId`;
3. hashes the exact outgoing body and assertion with lowercase SHA-256 hex;
4. builds `AdminMachineProof` from method, exact path/query, hashes, app/key IDs, timestamp, and idempotency key;
5. calls `AdminMachineProofVerifier.Canonicalize`;
6. signs that UTF-8 canonical string with HMAC-SHA256 and `CurrentSecret`;
7. writes standard Base64 and the six shared headers to a fresh downstream request.

`PreviousSecret` may be accepted by the API during rotation but is never selected for a new signature. The private assertion key and HMAC secret never become request headers.

### 6. Isolate inbound headers with a new downstream request

The BFF never clones `HttpRequest.Headers`. It constructs a new `HttpRequestMessage`, copies the small semantic allowlist, then sets generated authentication headers.

This blocks every `AdminIdentityHeaderPolicy.ForbiddenHeaders` member, client `X-Admin-*`, the Cloudflare assertion, `Authorization`, `Cookie`, `Host`, `Forwarded`, and arbitrary `X-Forwarded-*` values from crossing into the API.

### 7. Retain fail-closed configuration validation

The implemented Unit 1 validation remains unchanged:

| Section | Validation |
| --- | --- |
| `CloudflareAccess` | Issuer and audience required; one or more unique key IDs; parseable RSA public PEMs |
| `AdminAssertion` | Issuer, audience, app ID, key ID, and parseable RSA private PEM required |
| `AdminAppAuth` | Existing validation; exactly one app matches assertion app ID and has a current secret |
| `AdminApi` | Absolute HTTP(S) origin with root path and no query or fragment |
| `AllowedAdminSubjects` | Missing/empty allowed and authorises nobody; configured entries non-blank and exact-match |

The environment-key surface remains:

```text
CloudflareAccess__Issuer
CloudflareAccess__Audience
CloudflareAccess__Keys__0__KeyId
CloudflareAccess__Keys__0__PublicKeyPem
AdminAssertion__Issuer
AdminAssertion__Audience
AdminAssertion__AppId
AdminAssertion__KeyId
AdminAssertion__PrivateKeyPem
AdminAppAuth__Apps__0__AppId
AdminAppAuth__Apps__0__KeyId
AdminAppAuth__Apps__0__CurrentSecret
AdminAppAuth__Apps__0__PreviousSecret
AdminApi__BaseUrl
AllowedAdminSubjects__0
```

There are no application defaults for required values. `PreviousSecret` is optional. Cloudflare key rotation is configuration-driven; the BFF does not fetch JWKS over the network.

### 8. Keep health semantics narrow

Expose:

- `GET /health/live`: unconditional 200 after process startup.
- `GET /health/ready`: anonymous `GET` to the API's `/api/v1/health/live`; 2xx maps to 200, and non-2xx or connectivity failure maps to 503.

The readiness request carries no Cloudflare or admin-authentication headers. It proves that the BFF can reach its immediate internal dependency, not that it duplicates the API's deeper PostgreSQL/llama readiness policy.

### 9. Add an `admin` target from the existing container lineage

Extend the existing Dockerfile without changing `api` or `operator` behaviour:

- publish `Rag.AdminApp.Host` into `/out/admin` in `build`;
- add `FROM runtime AS admin`;
- copy the output as `rag:rag`;
- run as inherited UID 10001;
- set `ASPNETCORE_URLS=http://+:8080` and `DOTNET_EnableDiagnostics=0`;
- expose container port 8080 and run `dotnet Rag.AdminApp.Host.dll`.

The inherited runtime stage supplies the same ASP.NET base, curl, non-root user, work directory, and OCI version/revision labels as the existing application images. `EXPOSE 8080` is container metadata; `compose.coolify.yaml` still defines no host-published `ports`.

### 10. Extend publication as one three-image security transaction

Add `admin` package and promotion rows for `ghcr.io/jesusjbriceno/rag-adminapp` to develop and release workflows for native amd64 and arm64. Admin uses the existing architecture-collision checks, Trivy HIGH/CRITICAL gate, keyless cosign signing, SPDX SBOM, SLSA v1 provenance, multi-platform index promotion, proof artifact, and asynchronous verification.

The GHCR package visibility is locked to **public**. Before the first eligible publication is treated as complete, the operator verifies that the package exists and is public; repository auto-creation may still be used, but a newly auto-created private package must be switched to public before Coolify rollout. No registry credential is added to `compose.coolify.yaml`. This package-distribution choice is independent from service exposure: `admin` still has no published port and remains reachable only through the Cloudflare Access-protected Coolify route.

Evolve the completion payload from two images to exactly `api`, `operator`, and `admin`, with schema version 3, deterministic ordering, two platforms per image, and a negative fixture proving a missing admin proof cannot complete publication. Historical markers remain immutable.

Publication produces coordinated tags, but deployment validation intentionally keeps the AdminApp pin independent from the API/migrate pair. API and migrate must retain their existing same-tag/reference-kind invariant. Admin needs only its own valid immutable tag or digest so an operator can roll back the stateless BFF without rolling back the API or migration image.

### 11. Add AdminApp to the existing Coolify Compose stack

Add exactly one service named `admin` to `compose.coolify.yaml`; do not modify the `postgres`, `model-download`, `llama-cpp`, `migrate`, or `api` service definitions.

The service contract is:

| Field | Decision |
| --- | --- |
| Image | `ghcr.io/jesusjbriceno/rag-adminapp` plus required `${RAG_ADMINAPP_IMAGE_REFERENCE:?required}` suffix |
| Mutability | Immutable semver/develop tag or repository-specific sha256 digest only |
| Pull behaviour | `pull_policy: always`; no `build` |
| Network | Existing implicit/default Compose network only |
| API dependency | `AdminApi__BaseUrl` is a required reference whose resolved origin targets service `api` on container port 8080 |
| Ports | No `ports`; Coolify owns external routing |
| FQDN | The exact source mapping is `ADMINAPP_FQDN: ${ADMINAPP_FQDN:?required}` on `admin` only; no literal, default, alternate reference, or value |
| Configuration | All BFF settings are environment references; no embedded key, secret, subject, audience, issuer, or hostname |
| Health | `curl --fail --silent --show-error http://127.0.0.1:8080/health/ready` |
| Startup | Depend on healthy `api`; restart unless stopped |

The source reference map is explicit so the validator can distinguish configuration structure from values:

| Container key | Deployment reference |
| --- | --- |
| `ADMINAPP_FQDN` | `${ADMINAPP_FQDN:?required}` |
| `CloudflareAccess__Issuer` | `${CLOUDFLARE_ACCESS__ISSUER:?required}` |
| `CloudflareAccess__Audience` | `${CLOUDFLARE_ACCESS__AUDIENCE:?required}` |
| `CloudflareAccess__Keys__0__KeyId` | `${CLOUDFLARE_ACCESS__KEYS__0__KEY_ID:?required}` |
| `CloudflareAccess__Keys__0__PublicKeyPem` | `${CLOUDFLARE_ACCESS__KEYS__0__PUBLIC_KEY_PEM:?required}` |
| `AdminAssertion__Issuer` | `${ADMIN_ASSERTION__ISSUER:?required}` |
| `AdminAssertion__Audience` | `${ADMIN_ASSERTION__AUDIENCE:?required}` |
| `AdminAssertion__AppId` | `${ADMIN_ASSERTION__APP_ID:?required}` |
| `AdminAssertion__KeyId` | `${ADMIN_ASSERTION__KEY_ID:?required}` |
| `AdminAssertion__PrivateKeyPem` | `${ADMIN_ASSERTION__PRIVATE_KEY_PEM:?required}` |
| `AdminAppAuth__Apps__0__AppId` | `${ADMIN_APP_AUTH__APPS__0__APP_ID:?required}` |
| `AdminAppAuth__Apps__0__KeyId` | `${ADMIN_APP_AUTH__APPS__0__KEY_ID:?required}` |
| `AdminAppAuth__Apps__0__CurrentSecret` | `${ADMIN_APP_AUTH__APPS__0__CURRENT_SECRET:?required}` |
| `AdminAppAuth__Apps__0__PreviousSecret` | `${ADMIN_APP_AUTH__APPS__0__PREVIOUS_SECRET:-}` |
| `AdminApi__BaseUrl` | `${ADMIN_API__BASE_URL:?required}` |
| `AllowedAdminSubjects__0` | `${ALLOWED_ADMIN_SUBJECTS__0:?required}` |

`ADMINAPP_FQDN` is a deployment contract marker, not an application secret and not a claim that Compose itself configures the external route. Its only allowed source form is the exact required reference `${ADMINAPP_FQDN:?required}` in the `admin` environment mapping. The Compose source contains no FQDN value, fallback, alternate indirection, or second occurrence. The operator supplies the value externally and assigns that same FQDN to the `admin` resource in Coolify. No source-controlled Traefik/Caddy route or literal domain is introduced.

### 12. Extend the Coolify validator atomically

The current validator accepts exactly five services and already protects the production topology. Its legacy invariants are a preservation boundary, not an implementation detail. The AdminApp change must land atomically across `compose.coolify.yaml`, `scripts/validate-coolify-compose.py`, `scripts/validate-coolify-compose.sh`, and `scripts/test-validate-coolify-compose.py`: no intermediate accepted state may allow the new service without its checks or reject a valid legacy invariant because a check was replaced.

The preserved legacy contract is:

- exact legacy services `postgres`, `model-download`, `llama-cpp`, `migrate`, and `api` remain required within the expanded set;
- pinned downloader/server image digests, downloader user/entrypoint/mounts, artifact gates, single pinned HTTPS source, model volume permissions, fixed CPU/offline llama command, and no GPU runtime remain enforced;
- `api` and `migrate` retain their exact GHCR repository rules, pull-only policy, immutable-reference syntax, same reference-kind requirement, and same-tag requirement when tag-based;
- `api` continues to require `http://llama-cpp:8080/`;
- published ports remain rejected on every service;
- only the implicit/default Compose network remains allowed;
- existing failure diagnostics remain unchanged except where the exact service-set message and final success summary must name the added `admin` service.

The accepted service set expands in one transaction to exactly six names. The canonical manifest, documentation, and fixture order is `postgres`, `model-download`, `llama-cpp`, `migrate`, `api`, `admin`; semantic validation is exact-set validation and therefore does not depend on JSON object ordering. `admin` is the only service allowed to carry the exact FQDN marker or any domain-shaped configuration. Extend the existing validator rather than creating another deployment validator.

#### Validator input model

Reference-only validation cannot be proven from ordinary `docker compose config` output because interpolation replaces `${...}` expressions. Use two normalised views:

1. **Rendered view:** ordinary `docker compose config --format json`, used for image syntax, resolved internal URL, healthcheck, network, and topology checks.
2. **Source-preserving view:** `docker compose config --no-interpolate --format json`, used to require the exact environment/image/FQDN references and reject literal values.

`validate-coolify-compose.sh` supplies both views to `validate-coolify-compose.py` without printing resolved secret values. Temporary source-preserving data is cleaned with a shell trap; resolved configuration stays on stdin rather than in a persistent artifact. Focused tests call the same validation functions with paired fixtures.

#### Exact service and image rules

- Required services become exactly `postgres`, `model-download`, `llama-cpp`, `migrate`, `api`, and `admin` in the same atomic change as the admin-specific checks and fixtures; missing or additional services fail.
- Add `admin -> ghcr.io/jesusjbriceno/rag-adminapp` to immutable image validation.
- Admin rejects absent images, repository drift, empty or `latest` tags, malformed tags/digests, tag-plus-digest, local `build`, or a pull policy other than `always`.
- Preserve the existing API/migrate coordinated tag/reference-kind checks unchanged.
- Validate AdminApp independently so its rollback tag or digest need not equal API/migrate.

#### Admin configuration and health rules

- The source-preserving view must contain every required mapping in Decision 11 and no literal replacement for one of those mappings.
- `PreviousSecret` may be the sole optional empty reference; no secret default is accepted.
- `ADMINAPP_FQDN` must exist only on `admin` as the exact source reference `${ADMINAPP_FQDN:?required}`. A literal, fallback/default, alternate variable, generated value, or occurrence on another service fails.
- The rendered `AdminApi__BaseUrl` must parse as the internal root origin `http://api:8080/`; external hosts, HTTPS public origins, userinfo, query, fragment, or non-root paths fail.
- The admin healthcheck must invoke local `GET /health/ready` through curl; a missing healthcheck, another path, or another host fails.

#### Private-topology rules

- Reject `ports` on every service and reject `network_mode: host`.
- Require only the implicit/default Compose network; reject custom/external networks for this stack.
- Reject public-routing labels or domain/FQDN configuration on every non-admin service. The denied label/key families include Coolify-, Traefik-, Caddy-, `domain`-, and `fqdn`-shaped routing metadata; the only allowed FQDN/domain-shaped configuration is `ADMINAPP_FQDN: ${ADMINAPP_FQDN:?required}` on `admin`.
- Assert `api` still targets `http://llama-cpp:8080/` and keep all existing PostgreSQL/model/llama checks.
- Require `admin` to target the `api` service internally and never a host or public name.

These checks prove the repository topology has one eligible public service and that API/PostgreSQL/llama have no Compose-level publication mechanism. They deliberately do not claim to inspect Coolify dashboard domains, host firewall state, DNS, or Cloudflare policy. The runbook requires an operator-side check for those external surfaces.

### 13. Document Cloudflare Access as the FQDN boundary

Create `docs/deployment/adminapp-coolify.md` as the focused operator runbook and update the existing Coolify guide/README where they currently state that every service has no domain or that only two application images exist.

The runbook leads with this invariant:

> Assign an FQDN only to AdminApp, and bind that exact externally supplied FQDN to a Cloudflare Access application before enabling administrative access. Never assign a domain or published port to API, PostgreSQL, or llama.cpp.

It then documents, without values:

1. verify the chosen immutable AdminApp image and publication evidence;
2. supply image, BFF configuration, allowlist, and FQDN references in Coolify;
3. configure the Cloudflare Access application/policy externally for that FQDN;
4. configure Coolify routing only for `admin`, after the Access boundary is ready;
5. confirm unauthenticated traffic is stopped at the edge;
6. confirm valid Access identity but absent BFF allowlist membership is denied by the BFF;
7. allow one operator-selected subject, verify access, and create the first client through the proxied operation;
8. check liveness/readiness from an operator-controlled context;
9. rotate Cloudflare validation, assertion, and HMAC keys without logging material;
10. roll back by selecting a previous verified immutable AdminApp image reference.

Cloudflare team domain, application AUD, JWKS-derived keys, policy rules, FQDN, allowed subjects, TLS/DNS choices, and secret storage remain external inputs. Documentation uses placeholders only.

## Data Flows

### Authorised request

```text
request to operator FQDN
  -> Cloudflare Access policy
  -> Coolify admin route
  -> single Access assertion header
  -> CloudflareAccessValidator
  -> ordinal subject allowlist
  -> exact route descriptor
  -> bounded body + idempotency resolution
  -> AdminAssertionIssuer(subject, now)
  -> SHA256(body) + SHA256(assertion)
  -> AdminMachineProofVerifier.Canonicalize
  -> HMACSHA256(CurrentSecret)
  -> fresh downstream request with generated X-Admin-* headers
  -> http://api:8080/api/v1/admin/* on default network
  -> filtered response
```

Any failure before allowlist success creates no assertion or proof and makes no downstream call.

### Readiness

```text
Coolify/Docker healthcheck
  -> GET http://127.0.0.1:8080/health/ready
  -> anonymous GET http://api:8080/api/v1/health/live
  -> 2xx => 200
  -> non-2xx/network failure => 503
```

### Public-boundary configuration

```text
operator chooses values externally
  -> inject ADMINAPP_FQDN and BFF references into Coolify
  -> bind same FQDN to Cloudflare Access application
  -> assign Coolify domain to admin only
  -> verify edge denial and BFF denial/success paths
```

The Compose validator checks the first source-controlled topology; the operator checklist checks the external bindings.

## File-Level Change Plan

| Path | Planned change |
| --- | --- |
| `src/Rag.Infrastructure/AdminAuthenticationContract.cs` | **Preserve implemented Unit 1 file; no redesign change** |
| `src/Rag.Api/AdminAuthentication.cs` | **Preserve implemented Unit 1 aliases; no behavioural change** |
| `src/Rag.AdminApp.Host/Rag.AdminApp.Host.csproj` | **Preserve implemented Unit 1 project** |
| `src/Rag.AdminApp.Host/Program.cs` | **Preserve composition root; later mappings remain in extensions** |
| `src/Rag.AdminApp.Host/Configuration/*` | **Preserve implemented Unit 1 validation** |
| `Rag.sln` and Unit 1 tests/project references | **Preserve implemented Unit 1 state** |
| `src/Rag.AdminApp.Host/Auth/*` | Add Access-header parsing, token filter, and subject allowlist wrapper |
| `src/Rag.AdminApp.Host/Proxy/*` | Add closed operation catalog, typed API client, and `AdminProxyRequestSigner` |
| `src/Rag.AdminApp.Host/Health/*` | Add API liveness probe and BFF health mappings |
| `tests/Rag.UnitTests/*AdminAppHost*` | Add auth, operation-catalog, signer, and header-boundary tests |
| `tests/Rag.IntegrationTests/*AdminAppHost*` | Add nine-operation proxy/auth and health tests |
| `Dockerfile` | Publish host and add the `admin` runtime target |
| `compose.coolify.yaml` | Add only the `admin` service; leave five existing service definitions unchanged |
| `scripts/validate-coolify-compose.py` | Extend exact-service, immutable admin image, reference, health, and private-topology validation |
| `scripts/validate-coolify-compose.sh` | Feed rendered and source-preserving Compose views to the validator without logging secrets |
| `scripts/test-validate-coolify-compose.py` | Preserve legacy cases and add admin acceptance/rejection/topology fixtures |
| `.github/workflows/ci-pr.yml` | Keep Coolify validator tests and add admin Docker/workflow contract checks |
| `.github/workflows/ci-develop.yml` | Add admin package, promotion, and retention entries |
| `.github/workflows/ci-release.yml` | Add admin package, promotion, and retention entries |
| `scripts/ci-publication-marker.sh` | Require three-image schema v3 completion |
| `scripts/test-ci-publication-marker.sh` | Add three-image positive and missing-admin negative fixtures |
| `scripts/ci-publication-verifier.sh` and its tests | Verify admin index/platform evidence with API/operator evidence |
| workflow contract test | Prove admin uses the existing matrix security chain |
| `docs/deployment/adminapp-coolify.md` | Add focused FQDN, Access, onboarding, health, rotation, and rollback runbook |
| `docs/deployment/coolify.md` | Update private-boundary wording and two-image publication/pinning guidance to include AdminApp |
| `README.md` | Replace stale all-private/two-image/Dokploy wording with the one-public-AdminApp boundary |
| `compose.dokploy.yaml`, `scripts/validate-dokploy-compose.*`, Dokploy docs | **Do not create or modify; out of scope** |

Exact helper-file grouping may be reduced during implementation, but contracts, source boundaries, tests-with-behaviour, and rollback units remain fixed.

## Verification Strategy

Strict TDD applies to remaining executable work. Unit 1 evidence remains preserved; it is not recreated merely because the deployment design changed.

### Unit and integration tests

- Valid/invalid/expired Access JWTs, wrong issuer/audience/key, and missing/blank/multiple header values.
- Empty/non-member/member allowlist behaviour with no pre-authorisation credential issuance.
- Assertion claims and lifetime no greater than 60 seconds.
- Proof hashes, timestamp, idempotency key, canonical text, HMAC, and Base64 checked with the real verifier.
- `CurrentSecret` signs; `PreviousSecret` never signs.
- Every forbidden identity header is absent downstream.
- Exactly nine descriptors and one round-trip row per operation.
- Unsupported paths/methods and malformed GUIDs never contact the API.
- Oversized body, invalid idempotency headers, filtered response headers, 502 handling, and cancellation.
- Live 200; ready 200/503 according to API liveness reachability; no auth headers on readiness.
- Existing Unit 1 startup-validation suite remains green.

### Container and publication tests

- `dotnet restore`, `dotnet build`, and affected/full solution tests.
- `docker build --target admin .`.
- Inspect UID 10001, port/listen environment, diagnostics setting, entrypoint, and OCI labels.
- Assert exact admin package/promotion rows, architecture-runner pairing, repository, retention, and unchanged shared security steps.
- Run publication guard, marker, verifier, and multiarch-index tests; require three-image schema v3 and a missing-admin failure.
- Run actionlint.

### Coolify validator tests

Positive fixtures:

- exact six-service stack;
- valid admin semver tag;
- valid admin develop-SHA tag;
- valid repository-specific admin digest independent from API/migrate reference form;
- exact source references, internal API URL, no ports, default network, and correct readiness command.

Negative fixtures mutate one invariant at a time:

- missing `admin` or any extra service;
- wrong admin repository, missing/mutable/malformed/tag-plus-digest image, local build, or missing pull policy;
- missing required environment key, literal value replacing a reference, or non-reference secret/default;
- absent, literal, or non-admin `ADMINAPP_FQDN`;
- missing/wrong readiness healthcheck;
- `AdminApi__BaseUrl` targeting localhost, a host/public name, wrong service/port, userinfo, query, fragment, or non-root path;
- `ports` on admin, API, PostgreSQL, or llama.cpp;
- host networking or a custom/external network;
- routing/domain labels or FQDN configuration on API, PostgreSQL, llama.cpp, or another non-admin service.

Existing downloader/model, API repository, operator repository, API/migrate coordinated-reference, and API-to-llama tests remain mandatory. A shell integration test runs the real Compose file through both normalisation views.

Tests use generated ephemeral cryptographic material and explicit test-only placeholder hostnames. They contain no accepted production value.

### Operator verification

Automated validation cannot prove external dashboard or Cloudflare state. Before enabling access, the operator records evidence that:

- Coolify has a domain only on `admin`;
- API, PostgreSQL, and llama.cpp have no domains or host-published ports;
- the supplied FQDN is covered by a Cloudflare Access application;
- an unauthenticated request is blocked at the edge;
- a valid but non-allowlisted identity is blocked by the BFF;
- an allowlisted identity reaches one supported operation;
- liveness/readiness are healthy.

## Work Units and Rollback

| Unit | State/deliverable | Same-unit verification | Rollback |
| --- | --- | --- | --- |
| 1. Shared contract and validated host shell | **Implemented; preserve** | Existing focused Unit 1 tests and full solution regression | Revert only through the already-defined Unit 1 atomic rollback; this redesign makes no Unit 1 edit |
| 2. Authentication, assertion, proof, exact proxy | Pending | Unit + integration tests against real API verifiers | Remove BFF behaviour/tests while retaining the inert validated host shell |
| 3. Health and admin image | Pending | Health integration + admin target build/inspection | Remove health additions and admin target; API/operator targets remain |
| 4. Coolify AdminApp boundary and runbook | Pending; replaces former Dokploy unit | Paired-view validator tests + real Compose validation + docs checklist | Remove only `admin`, admin validator branches/fixtures, and AdminApp docs; restore exact five-service set; existing service definitions and data stay unchanged |
| 5. Three-image publication chain | Pending | Workflow contract, schema-v3 marker/verifier, actionlint | Revert admin workflow rows and schema/tests atomically before a later publication; historical images/records stay immutable |

Each pending unit remains a cohesive conventional-commit candidate with its tests and documentation. Tasks must retain Unit 1 checkmarks and regenerate only stale Unit 2-5/Dokploy references. If the forecast remains above the review budget, these units are natural chained-PR slices; tests are never split from behaviour to reduce line count.

## Rollout

1. Keep Unit 1 unchanged and implement Units 2-3 under strict TDD.
2. Add the admin image to the existing CI security chain and verify a future eligible workflow produces all admin platform/index evidence. Before rollout, verify `ghcr.io/jesusjbriceno/rag-adminapp` exists as a public GHCR package; if first push auto-creates it as private, change its visibility to public as a separately authorised operational action. Implementation itself performs no publication.
3. Add the `admin` Compose service, paired-view validator, and runbook atomically; verify the five existing service blocks are unchanged.
4. As a separately authorised operational action, choose and verify an immutable AdminApp image reference and supply all required values in Coolify.
5. Configure the Cloudflare Access application for the operator-selected FQDN before enabling administrative access.
6. Assign that FQDN only to `admin` in Coolify; confirm API, PostgreSQL, and llama.cpp still have no domain or host port.
7. Deploy, wait for readiness, and run edge-denial, BFF-denial, allowlisted-operation, and health checks.
8. Record the deployed immutable reference and the preceding verified reference for rollback.

Deployment rollback changes only `RAG_ADMINAPP_IMAGE_REFERENCE` to a previous verified immutable tag/digest and redeploys AdminApp. There is no BFF database, content migration, or internal-service rollback. If the public boundary is unsafe, disable/remove the AdminApp route first, preserving internal services, then correct Cloudflare/Coolify configuration before re-enabling it.

## Requirement Traceability

| Requirement | Design coverage |
| --- | --- |
| Fail-closed Cloudflare authentication and allowlist | Decisions 4 and 7; unit/integration rejection tests |
| Verifiable downstream identity | Decisions 2 and 5; real-verifier compatibility tests |
| Exactly nine operations | Decision 3; descriptor and no-downstream-call tests |
| Header boundary | Decision 6; enumerable denylist tests |
| Health | Decision 8; health integration and Compose healthcheck |
| Configuration-only secrets | Decisions 7, 11, and 12; source-preserving validation |
| Admin image | Decision 9; image build/inspection |
| Secure multiarchitecture publication | Decision 10; workflow/schema-v3 tests |
| Coolify integration and sole public boundary | Decisions 11-12; exact-service and topology checks |
| Cloudflare Access FQDN boundary | Decision 13; external rollout gates |
| Focused verification | Verification Strategy |
| Runbook and rollback | Decision 13, Work Units, and Rollout |
| Deferred frontend and no extra deployment target | Decisions 1 and 11; file plan no-create boundary |
| Preserve Unit 1 | Current Baseline, file plan, work units, and rollout |

## Risks and Mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| External Coolify dashboard assigns a domain to an internal service | High | Validator blocks source-level publication; runbook requires explicit dashboard evidence for every service before enablement |
| Admin FQDN is reachable without Cloudflare Access | High | Access binding is a rollout gate before Coolify route enablement; verify edge denial before onboarding |
| A rendered Compose-only validator accepts literal secrets because interpolation erased provenance | High | Validate paired rendered and `--no-interpolate` views and test literal substitutions |
| Validator expansion regresses existing model/API guarantees | High | Preserve legacy checks/tests; add admin branches rather than replacing downloader, image, or API-to-llama validation |
| Admin rollback is blocked by API/migrate coordinated-tag policy | Medium | Validate admin immutability independently while preserving API/migrate pair invariants |
| Admin points to a public or attacker-controlled API origin | High | Require source reference and resolved `http://api:8080/` internal origin |
| Route metadata appears on API/PostgreSQL/llama | High | Reject ports, host networking, non-default networks, and public-routing/FQDN metadata on non-admin services |
| Canonicalisation/header drift breaks API authentication | High | Shared constants, direct `Canonicalize` reuse, and compatibility tests against real validators |
| Generic proxy exposes new API surface | High | Closed nine-entry catalog, no catch-all, and one test per route/method |
| Existing local Compose workflow now needs AdminApp inputs | Medium | Document required AdminApp references and keep validation sentinels ephemeral; do not add production secrets or defaults to source |
| GHCR AdminApp repository cannot be created/pushed or remains private after first push | Medium | Make existence and public visibility an operator-owned pre-publication/rollout gate; the package must be public before Coolify pulls it without registry credentials |
| Documentation retains stale all-private/two-image/Dokploy statements | Medium | Update README and existing Coolify guide in the same documentation work unit |
| Review workload exceeds the bounded budget | Medium | Preserve Unit 1; implement remaining rollback-safe units separately and keep tests with each behaviour |

## Explicit Non-Decisions

This design does not choose an AdminApp FQDN, Cloudflare team domain, Access application AUD, JWKS key IDs/public keys, Access policy, administrator subjects, assertion issuer/audience, app/key IDs, HMAC/RSA secrets, image tag/digest, DNS/TLS settings, Coolify secret backend, or host firewall rules. GHCR package visibility is decided: `ghcr.io/jesusjbriceno/rag-adminapp` is public. The mechanism and timing used to create the package or change its visibility remain an operator-owned action, subject to the pre-publication/rollout gate above. It also does not choose or create a Dokploy resource, Tailscale configuration, SPA, browser test suite, or new API endpoint.
