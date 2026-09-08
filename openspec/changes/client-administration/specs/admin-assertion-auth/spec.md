# Admin Assertion Authentication Specification

## Purpose

Authorize internal administration as a verified person acting through a trusted internal app, without creating an admin client credential.

## Requirements

### Requirement: Cloudflare-Verified Internal Assertions

Cloudflare Access MUST authenticate persons only to the minimal admin app in the same private Dokploy environment. The app MUST cryptographically validate the Cloudflare credential before issuing a short-lived, signed assertion containing a non-empty verified subject, issuer, audience, expiry, and unique replay identifier. Assertion signing algorithm, key rotation mechanics, and app packaging remain design decisions; they MUST support separate, rotatable trust material and MUST NOT expose secrets in artifacts.

#### Scenario: Verified person receives an assertion

- GIVEN an authorized person presents a cryptographically valid Cloudflare Access credential to the internal app
- WHEN the app creates an assertion for an admin request
- THEN it returns a signed, short-lived assertion with the required claims

#### Scenario: Invalid Cloudflare credential is rejected

- GIVEN a Cloudflare credential is absent, expired, or fails cryptographic validation
- WHEN the person requests an assertion
- THEN no assertion is issued and no identity is forwarded

### Requirement: Dual API Authentication and Replay Rejection

Every listed `/api/v1/admin/*` endpoint MUST be internal-only and require both trusted-app machine authentication and a valid internal assertion. The API MUST verify assertion issuer, audience, expiry, signature, non-empty subject, and unused replay identifier before executing a handler; it MUST atomically reject assertion replay. The API MUST reject raw Cloudflare or proxy identity headers as authorization evidence. The deployment secret authenticates only the app as a machine, MUST be independently rotatable, and MUST NOT be a `ServiceClient` or client credential. Its scheme remains a design decision.

#### Scenario: Dual authentication authorizes an endpoint

- GIVEN the request originates on the private admin path with valid machine authentication and assertion
- WHEN it targets an administrative endpoint
- THEN the API authorizes it with the assertion subject as the human actor

#### Scenario: Missing, forged, or replayed trust is rejected

- GIVEN either machine authentication or any required assertion check is invalid, or the replay identifier was used
- WHEN the request targets an administrative endpoint
- THEN the API returns a non-sensitive authentication failure and performs no mutation

### Requirement: Complete Endpoint Protection and Isolation Tests

The protected contract SHALL be: `POST/GET /clients`, `GET /clients/{id}`, `POST/GET /clients/{id}/credentials`, `GET /credentials/{id}`, `POST /credentials/{id}/rotate`, `POST /credentials/{id}/revoke`, and `GET /audit`. Authorization and isolation tests MUST prove every route rejects public, service-client JWT-only, raw-header-only, and one-factor requests.

#### Scenario: Service client cannot administer

- GIVEN a valid integration service-client JWT without app authentication and assertion
- WHEN it invokes each administrative route
- THEN each route rejects the request without revealing administrative data
