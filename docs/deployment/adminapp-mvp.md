# AdminApp MVP deployment runbook

This runbook describes how to deploy the admin browser application
(`rag-adminapp`: the Cloudflare-guarded BFF `Rag.AdminApp.Host` plus the admin
SPA) as a private service next to the existing API stack. It is a
**future-deployable** contract: nothing in this repository deploys, publishes,
or configures a live environment. The test hostname selected for this stack is
`admin-rag.kitin.es`; DNS, Access policies, subjects, keys, and secrets remain
operator-supplied. No DNS record, Cloudflare policy, or secret is created here.

The service is delivered by Compose (`compose.coolify.yaml`, service
`rag-adminapp`) and validated by `scripts/validate-coolify-compose.py`. It has
**no published ports**: it is reachable only through the platform proxy in front
of it, which must sit behind Cloudflare Access.

## Prerequisites

Before the first admin deployment, the environment must already provide:

1. The existing stack (`rag-postgres`, `rag-llama-cpp`, `rag-migrate`, `rag-api`)
   deployed and healthy per `docs/deployment/coolify.md`.
2. A published `ghcr.io/jesusjbriceno/rag-adminapp` image at the immutable tag
   the deployment references. The Compose default follows the API release train
   (`RAG_ADMINAPP_IMAGE_REFERENCE` falls back to `RAG_API_IMAGE_REFERENCE`);
   the admin image is published by the same release pipeline (see
   `.github/workflows/ci-release.yml` / `ci-develop.yml`). If the tag exists for
   the API but not yet for the admin image, pull fails — publish or pin an
   existing admin tag first.
3. A Cloudflare Access application in front of `admin-rag.kitin.es`, with a
   policy that the operator defines. The repository does not provision Cloudflare
   policy; the BFF only verifies the JWT and enforces the subject allowlist.
4. The Cloudflare Access team domain (issuer), application AUD (audience), and
   the public key(s) Cloudflare publishes for JWT verification.
5. The opaque Cloudflare Access subject(s) of the administrators who may operate
   the BFF (see onboarding below).
6. A non-production assertion signing key pair for the BFF→API machine proof
   (the BFF holds the private PEM; the API holds the matching public PEM).

## Exact configuration contract

### `rag-adminapp` service (operator-supplied, fail closed)

All values come from the deployment environment (Coolify environment variables
or the deployment `.env`). The BFF validates them **at startup** and refuses to
start — it never serves the admin surface unconfigured.

| Compose variable | Feeds | Meaning | Required |
| --- | --- | --- | --- |
| `ADMINBFF__ALLOWED_ORIGIN` | `AdminBff:AllowedOrigin` | Exact browser origin (scheme + host) allowed to call mutation routes. For this test set `https://admin-rag.kitin.es`, matching the Coolify domain exactly. | Yes |
| `ADMINBFF__ALLOWED_SUBJECTS__0` | `AdminBff:AllowedSubjects:0` | One exact Cloudflare Access subject. Add more via `ADMINBFF__ALLOWED_SUBJECTS__1`, `__2`, … An empty list means no administrator may operate the BFF. | Yes (at least one) |
| `ADMINBFF__SIGNING__APP_ID` | `AdminBff:Signing:AppId` (and `AdminAssertion:AppId`) | The admin app id the API's `AdminAppAuth` expects. | Yes |
| `ADMINBFF__SIGNING__KEY_ID` | `AdminBff:Signing:KeyId` (and `AdminAssertion:KeyId`) | Key id for the HMAC machine proof and the assertion. | Yes |
| `ADMINBFF__SIGNING__MACHINE_SECRET` | `AdminBff:Signing:MachineSecret` | HMAC shared secret matching `AdminAppAuth__Apps__0__CURRENT_SECRET` on the API. Secret — never log it. | Yes |
| `CLOUDFLAREACCESS__ISSUER` | `CloudflareAccess:Issuer` | Cloudflare Access issuer (team domain URL). Operator-supplied. | Yes |
| `CLOUDFLAREACCESS__AUDIENCE` | `CloudflareAccess:Audience` | Cloudflare Access application AUD. Operator-supplied. | Yes |
| `CLOUDFLAREACCESS__KEYS__0__KEY_ID` / `__PUBLIC_KEY_PEM` | `CloudflareAccess:Keys:0` | Cloudflare Access JWT verification key. Additional keys via `__1`, … | Yes (at least one) |
| `ADMINASSERTION__ISSUER` / `__AUDIENCE` | `AdminAssertion:Issuer` / `Audience` | Assertion issuer/audience. Must match the API's `ADMINASSERTION__ISSUER` / `__AUDIENCE`. | Yes |
| `ADMINASSERTION__PRIVATE_KEY_PEM` | `AdminAssertion:PrivateKeyPem` | Assertion signing private key (single-line PEM with `\n` escapes, like the existing `JWT__…__PRIVATE_KEY_PEM` pattern). Secret — never log it. | Yes |

Fixed (not operator-supplied) wiring inside the Compose service:
`AdminBff__UpstreamBaseUrl=http://rag-api:8080/` (private network only) and
`AdminBff__SpaRoot=/app/wwwroot` (the SPA bundled in the image).

## Coolify hostname and DNS

For the test deployment, use `https://admin-rag.kitin.es`:

1. In the DNS zone for `kitin.es`, point `admin-rag.kitin.es` at the Coolify
   server's public proxy target using the appropriate A/AAAA or CNAME record.
   When using Cloudflare Access, proxy the record through Cloudflare. The server's
   address is environment-specific and is not stored in this repository.
2. In the Coolify resource's **Domains** settings, add an HTTPS domain for
   `admin-rag.kitin.es`, select the `rag-adminapp` service, and set the internal
   port to `8080`. Coolify routes through its proxy; do not add `ports:` to
   Compose. See [Coolify Domains](https://coolify.io/docs/core/networking/domains).
3. Set `ADMINBFF__ALLOWED_ORIGIN=https://admin-rag.kitin.es` exactly, then create
   a Cloudflare Zero Trust Access application for the same hostname. Restrict its
   policy to the intended administrators and configure the issuer, audience, and
   verification keys listed above.
4. Keep `rag-api` and the data services without public domains. Only the admin
   browser surface receives this hostname, and it remains guarded by both
   Cloudflare Access and the BFF's verified-subject allowlist.

### `rag-api` admin plane (off unless explicitly enabled)

| Compose variable | Feeds | Meaning | Default |
| --- | --- | --- | --- |
| `ADMINPLANE__ENABLED` | `AdminPlane:Enabled` | Master switch. The admin plane exists only when this is exactly `true`. | `false` |
| `ADMINAPPAUTH__APPS__0__APP_ID` / `__KEY_ID` / `__CURRENT_SECRET` | `AdminAppAuth:Apps:0` | Machine-proof credential accepted from the BFF. `__PREVIOUS_SECRET` is optional for rotation. | empty |
| `ADMINASSERTION__ISSUER` / `__AUDIENCE` | `AdminAssertion:Issuer` / `Audience` | Must match the BFF's assertion issuer/audience. | empty |
| `ADMINASSERTION__VALIDATION_KEYS__0__KEY_ID` / `__PUBLIC_KEY_PEM` | `AdminAssertion:ValidationKeys:0` | Public key matching the BFF's `ADMINASSERTION__PRIVATE_KEY_PEM`. Additional keys via `__1`, … | empty |

When `ADMINPLANE__ENABLED=true` but trust material is missing or invalid, the
API fails at startup (`ValidateOnStart`) — it never runs with a half-configured
admin plane. The Compose validator additionally rejects an enabled plane without
the required trust keys.

### Image references

| Variable | Meaning |
| --- | --- |
| `RAG_ADMINAPP_IMAGE_REFERENCE` | Optional override; defaults to `RAG_API_IMAGE_REFERENCE` so the admin image follows the API release train. Must be an immutable tag (`vX.Y.Z[-prerelease]`) or `@sha256:<digest>` — never `latest`. |

## First-admin (allowlist) onboarding

1. In Cloudflare, create the Access application for the admin hostname and a
   policy the operator owns. The repository does not manage Cloudflare policy.
2. Determine each administrator's **opaque subject** exactly as Cloudflare
   places it in the verified identity token (the value the BFF reads after
   signature verification — browser-supplied identity headers are never
   trusted). Operator-supplied; never guessed.
3. Set `ADMINBFF__ALLOWED_SUBJECTS__0` (and `__1`, …) to those exact subjects
   and `ADMINBFF__ALLOWED_ORIGIN` to the admin origin, then redeploy the
   `rag-adminapp` service.
4. Grant the API side first: set `ADMINPLANE__ENABLED=true` plus the
   `ADMINAPPAUTH__*` and `ADMINASSERTION__*` trust values on `rag-api`, and
   redeploy it. The API refuses to start if the material is incomplete.
5. Verify: an unauthenticated request to the admin hostname must not reach the
   SPA shell or any `/api/admin` route (Cloudflare Access intercepts first; the
   BFF re-verifies and allowlists even if Access were bypassed). A request from
   a non-allowlisted verified identity is rejected. `/health` is the only
   unauthenticated probe.

## Health and security checks

- `GET /health` on `rag-adminapp` returns `{"status":"healthy"}` and is the only
  unauthenticated route. The Compose healthcheck targets it locally
  (`127.0.0.1:8080/health`), so an unhealthy/misconfigured container is reported
  by the platform without exposing anything.
- The service publishes no ports and runs as non-root (uid `10001`). Only built
  static SPA assets and the published host output are in the image — no Node, no
  source tree, no signing material beyond the injected environment.
- The BFF signs upstream requests server-side (assertion + HMAC machine proof)
  and only proxies the two fixed admin operations. Assertion private keys, HMAC
  secrets, and customer API secrets never reach the browser.
- Cross-origin mutations are rejected at the BFF before any API call is made.
- After deployment, confirm: `docker compose -f compose.coolify.yaml config`
  passes with the deployment's values; `rag-adminapp` reaches healthy; the SPA
  shell loads only for allowlisted identities; the issued secret appears exactly
  once and is not present in logs or storage.

## Secret handling and rotation

- Every admin secret lives in the deployment environment of the container that
  needs it. Never log `ADMINBFF__SIGNING__MACHINE_SECRET`,
  `ADMINAPPAUTH__…__CURRENT_SECRET`, `ADMINASSERTION__PRIVATE_KEY_PEM`, or
  Cloudflare keys. Rotating a value is a deployment change, not a code change.
- HMAC machine-proof secret (the API accepts current and optional previous;
  the BFF signs with only one secret):
  1. On `rag-api`, set `ADMINAPPAUTH__APPS__0__CURRENT_SECRET` to the new value
     and `ADMINAPPAUTH__APPS__0__PREVIOUS_SECRET` to the old value; redeploy.
     Keep the values distinct — equal current/previous secrets fail API startup.
  2. After `rag-api` is healthy with both distinct values configured, set
     `ADMINBFF__SIGNING__MACHINE_SECRET` to the new value on `rag-adminapp` and
     redeploy.
  3. Wait at least 60 seconds so in-flight requests signed with the old value
     have expired. The Coolify service uses the API's current fixed
     `AdminAppAuth.ClockSkewSeconds` default of 60 seconds.
  4. Remove `ADMINAPPAUTH__APPS__0__PREVIOUS_SECRET` on `rag-api`, keeping the
     new current value, then redeploy. Compose resolves the omitted optional
     value as an empty string; `AdminAppAuthOptions.Validate()` normalizes that
     empty value to `null`, so the API no longer accepts an empty-secret HMAC.
- Assertion signing key: generate a new key pair; add the new public key to the
  API as `ADMINASSERTION__VALIDATION_KEYS__1__*` and redeploy the API; then swap
  `ADMINASSERTION__PRIVATE_KEY_PEM` (and its `KEY_ID`) on the BFF; finally
  remove the old validation key from the API after the overlap window.
- Cloudflare Access keys: add the new `CLOUDFLAREACCESS__KEYS__n__*` entry
  before removing the old one; Cloudflare rotates its signing keys
  independently of this stack.

## Rollback

- Turn the surface off without touching anything else:
  `ADMINPLANE__ENABLED=false` on `rag-api` (redeploy). The admin plane and its
  endpoints disappear; the rest of the stack is unaffected.
- To remove the admin service entirely, stop/remove `rag-adminapp` in the
  platform. Other services do not depend on it.
- To roll the image back, pin the previous immutable
  `RAG_ADMINAPP_IMAGE_REFERENCE` (or the previous API train tag, which the
  default follows) and redeploy. Tags are immutable, so a rollback is a
  reference change, never a re-push.

## Publication notes

`ci-pr.yml` builds and tests the admin UI and builds the admin image locally
(no publish). `ci-release.yml` and `ci-develop.yml` publish, scan, sign, and
attest `ghcr.io/jesusjbriceno/rag-adminapp` alongside the API and operator
images. The publication completion marker (`scripts/ci-publication-marker.sh`)
durably records all three images — `adminapp`, `api`, and `operator` — at
`schema_version: 2`: the completion deployment is created only when every
image's index, both platform digests, signatures, and attestations are present,
and it fails closed if any component's proof is missing, mismatched, or
malformed. The asynchronous verifier (`scripts/ci-publication-verifier.sh`)
consumes the same marker generically and verifies each recorded image.
