# Client administration API: committed boundary

This document describes the client-administration API present in commit `f220b41`. It is a configuration-gated API plane, not a deployment guide for an admin application or platform.

## What the API delivers

Set `AdminPlane:Enabled=true` to map the internal `/api/v1/admin` route group and register its `Admin` authentication scheme, `AdminPlane` authorization policy, observability middleware, validation options, and retention worker. With the flag disabled, those gated components are not registered or mapped. The admin repository and application handlers are registered independently of the flag; this document does not describe disabled mode as having no admin services.

Every mapped route requires the `AdminPlane` policy. The authentication handler requires both:

1. A signed internal assertion in `X-Admin-Assertion`, validated for issuer, audience, RS256 signing key, lifetime, subject, app identifier, and replay identifier.
2. A machine proof over the request method, path/query, body hash, assertion hash, app/key identifiers, timestamp, and idempotency key. The proof is HMAC-SHA256-verified with configured current or previous application secret material.

The API rejects forbidden forwarded identity headers and reserves assertion replay identifiers before producing an `AdminActor`. It does not trust an operator identity supplied in a request header.

## API surface

The committed route group includes:

| Area | Routes |
| --- | --- |
| Clients | `POST /clients`, `GET /clients`, `GET /clients/{clientId}` |
| Credentials | `POST /clients/{clientId}/credentials`, `GET /clients/{clientId}/credentials`, `GET /credentials/{credentialId}`, `POST /credentials/{credentialId}/rotate`, `POST /credentials/{credentialId}/revoke` |
| Audit | `GET /audit` |

Client and audit listing use cursor pagination. Credential mutation routes require an idempotency key and `If-Match` version. Credential issue and rotation return secret material through their delivery result; the stored operation result is designed to hold only a safe result, not the secret.

## Required API configuration

When the plane is enabled, startup validation requires these API-owned sections:

| Section | Required purpose |
| --- | --- |
| `AdminPlane:Enabled` | Enables the admin API plane. |
| `AdminAssertion` | Expected issuer and audience plus one or more public validation keys. |
| `AdminAppAuth` | One or more application identifiers, key identifiers, and current machine-proof secrets; a previous secret is optional for rotation. |
| `AdminAudit` | Retention mode and retention days. |
| `AdminOperations` | Positive operation-retention hours. |

Treat assertion private keys and machine-proof secrets as deployment secrets. They must not be committed or written to logs.

## Retention and logs

When enabled, the retention worker purges expired assertion replays, aged idempotency operations, and audit records according to the configured policy. It writes a system retention audit event. `indefinite` audit retention skips audit-record deletion.

The admin observability middleware emits one structured record for an admin request using its route path, status, latency, verified actor/app claims, authentication-failure category, and problem code. Its implementation does not read request headers, assertions, bodies, secrets, hashes, or salts for that record. This is structured logging, not a delivered metrics integration.

## External issuer and host: not delivered

The API validates an internal assertion; it does **not** include an external assertion issuer, an admin application host, or a Cloudflare integration. A deployment that enables this plane must supply an external issuer/host that authenticates an operator, produces assertions compatible with the configured `AdminAssertion` issuer/audience/key set, and creates the machine proof. Those requirements are outside the committed API and are not verified here.

No Dokploy, Coolify, Cloudflare, or other platform deployment is delivered or prescribed by `f220b41`.

## Evidence boundary

The statements above are source-audited against `f220b41`. They are not an operational deployment certification, end-to-end issuer proof, or full-suite test result. Run environment-specific configuration and runtime validation before enabling the plane in any deployment.
