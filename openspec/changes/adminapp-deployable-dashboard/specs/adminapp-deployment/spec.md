# AdminApp Deployment Specification

## Purpose

Provide a deployable AdminApp backend-for-frontend (BFF) that authenticates authorised administrators, proxies the supported administrative surface to the existing API, and is published and operated through the project's established container-security guarantees.

## Requirements

### Requirement: Fail-closed Cloudflare Access authentication

The AdminApp BFF MUST validate a Cloudflare Access JWT on every administrative request using RS256 and configured issuer, audience, and validation keys. The BFF MUST fail startup when required Cloudflare Access configuration is absent. A request without a valid, current token MUST receive 401 and MUST NOT cause an assertion or machine proof to be issued.

#### Scenario: Valid Cloudflare Access token

- GIVEN the BFF has complete Cloudflare Access configuration
- AND a request contains a valid RS256 JWT for that configuration
- WHEN the request reaches an administrative BFF operation
- THEN the BFF SHALL continue to administrator authorisation

#### Scenario: Missing, invalid, or expired token

- GIVEN an administrative BFF operation
- WHEN its request has no valid current Cloudflare Access JWT
- THEN the BFF MUST return 401
- AND the BFF MUST NOT issue an assertion or machine proof

#### Scenario: Missing Cloudflare configuration

- GIVEN required Cloudflare Access configuration is absent
- WHEN the BFF starts
- THEN startup MUST fail before the BFF accepts administrative requests

### Requirement: Administrator allowlist enforcement

The BFF MUST authorise only Cloudflare Access subjects in its configured administrator allowlist. An absent or empty allowlist MUST authorise no subject. A valid but non-allowlisted subject MUST receive 403 and MUST NOT cause an assertion or machine proof to be issued.

#### Scenario: Authorised and unauthorised subjects

- GIVEN requests have passed Cloudflare Access validation
- WHEN a subject is in the configured allowlist
- THEN the request MUST be eligible for a supported administrative operation
- WHEN a subject is absent from the allowlist or the allowlist is empty
- THEN the BFF MUST return 403 without issuing an assertion or machine proof

### Requirement: Verifiable downstream administrator identity

For each authorised administrative request that is proxied downstream, the BFF MUST issue a 60-second RS256 administrator assertion and generate an HMAC-SHA256 machine proof compatible with the existing administrative API verifier. The assertion and proof MUST bind the authorised identity and verifier-required canonical request data. Private assertion keys and machine-proof secrets MUST remain unavailable to clients and MUST NOT be forwarded to the API.

#### Scenario: Authorised proxy request

- GIVEN an allowlisted request has passed Cloudflare Access validation
- WHEN the BFF proxies a supported administrative operation
- THEN the downstream request MUST include a signed assertion valid for no more than 60 seconds
- AND it MUST include a verifier-compatible machine proof

### Requirement: Restricted administrative proxy surface

The BFF MUST proxy exactly the nine existing `/api/v1/admin/*` operations and MUST NOT proxy other API paths or methods. For each supported operation, it MUST preserve the operation's expected request and response semantics while adding only required downstream identity credentials.

#### Scenario: Supported and unsupported operations

- GIVEN the existing administrative API exposes its nine supported operations
- WHEN an authorised client invokes a supported operation through the BFF
- THEN the BFF MUST forward the corresponding operation and preserve its response semantics
- WHEN a request targets another API path or method
- THEN the BFF MUST NOT proxy it

### Requirement: Downstream identity header boundary

Before proxying an authorised request, the BFF MUST strip client-supplied headers forbidden by the administrative identity-header policy. Only identity headers created by the BFF for the authorised request MAY be sent downstream.

#### Scenario: Client-supplied identity headers

- GIVEN a client sends a supported administrative request with forbidden identity headers
- WHEN the BFF proxies the request
- THEN client-supplied forbidden header values MUST NOT reach the API
- AND the API MUST receive only BFF-generated downstream identity credentials

### Requirement: Health semantics

The BFF MUST expose `GET /health/live` as a liveness endpoint and `GET /health/ready` as a readiness endpoint. Liveness MUST return 200 while the process is running. Readiness MUST return 200 only when the configured administrative API is reachable and MUST return 503 otherwise.

#### Scenario: API reachability changes readiness

- GIVEN the BFF process is running
- WHEN the configured administrative API is reachable and `/health/ready` is requested
- THEN the endpoint MUST return 200
- WHEN the configured administrative API is not reachable and `/health/ready` is requested
- THEN the endpoint MUST return 503

### Requirement: Configuration-only secret surface

The BFF and deployment artifacts MUST obtain Cloudflare Access validation, assertion, AdminApp authentication, internal API endpoint, and administrator allowlist settings through configuration and environment injection. They MUST provide no secret defaults and MUST NOT contain hardcoded secret or operational values. The deployment configuration MUST reference, rather than define, the AdminApp FQDN and all Cloudflare Access, assertion, AdminApp authentication, API, and allowlist values.

#### Scenario: Secret-free distributable configuration

- GIVEN source-controlled BFF, Compose, CI, and runbook artifacts
- WHEN they are inspected
- THEN they MUST contain no secret, Cloudflare team-domain, audience, key, FQDN, or administrator-subject value
- AND required settings MUST be injectable through configuration or environment references

### Requirement: Admin container target

The repository Dockerfile MUST provide an `admin` target that publishes and runs the BFF. The target MUST use the same base-layer lineage as the existing API and operator targets, listen on port 8080, disable .NET diagnostics, run as user `rag` with UID 10001, and publish OCI version and revision labels.

#### Scenario: Admin image build and runtime identity

- GIVEN the repository Dockerfile and build context
- WHEN `docker build --target admin .` succeeds and the resulting image is inspected
- THEN the image MUST run the AdminApp BFF as UID 10001
- AND it MUST expose the BFF on port 8080 with .NET diagnostics disabled
- AND it MUST contain OCI version and revision labels

### Requirement: Secure multi-architecture publication

Develop and release CI workflows MUST publish `ghcr.io/jesusjbriceno/rag-adminapp` for amd64 and arm64 with the same security chain used for `rag-api` and `rag-operator`: architecture-tag collision checking, HIGH/CRITICAL Trivy scanning, keyless cosign signing, SPDX SBOM attestation, SLSA v1 provenance attestation, multi-platform index promotion, publication-proof output, and asynchronous verification.

#### Scenario: Admin publication candidate

- GIVEN a develop or release workflow builds the admin component for amd64 and arm64
- WHEN the component is eligible for publication
- THEN CI MUST complete the stated scan, signing, SBOM, provenance, collision-check, index-promotion, proof, and asynchronous-verification chain
- AND the published multi-platform image reference MUST be `ghcr.io/jesusjbriceno/rag-adminapp`

### Requirement: Coolify Compose integration and public boundary

`compose.coolify.yaml` MUST define an `admin` service using an immutable `ghcr.io/jesusjbriceno/rag-adminapp` image reference and `pull_policy: always`. The service MUST be pull-only and MUST NOT define a local build. It MUST declare no published ports, use the existing Compose network for internal API connectivity, and define a readiness healthcheck against `GET /health/ready`.

The AdminApp FQDN MUST be configurable only through an environment-variable reference for the admin service. AdminApp MUST be the only service publicly reachable through Coolify routing, and Cloudflare Access MUST be the public access boundary for that FQDN. API, PostgreSQL, and llama.cpp MUST remain internal to the Docker network, with no published ports or public domain configuration.

#### Scenario: Valid private-service topology

- GIVEN `compose.coolify.yaml` is configured for AdminApp
- WHEN the admin and existing service definitions are inspected
- THEN `admin` MUST use an immutable GHCR reference with `pull_policy: always` and no local build or published ports
- AND the admin FQDN MUST be an environment-variable reference without a concrete hostname
- AND only AdminApp MUST have public Coolify routing
- AND API, PostgreSQL, and llama.cpp MUST have neither published ports nor public domain configuration and MUST remain reachable only through the internal Docker network

#### Scenario: Cloudflare Access boundary

- GIVEN an operator configures the AdminApp FQDN in Coolify
- WHEN the documented public-access procedure is followed
- THEN the FQDN MUST be bound to a Cloudflare Access application before administrative access is enabled
- AND the procedure MUST NOT define a Cloudflare team domain, audience, JWKS key, or access-policy decision

### Requirement: Atomic Coolify Compose validation coverage

The Coolify Compose validator, its required-service checks, and its acceptance and rejection test fixtures MUST be updated atomically with the AdminApp Compose integration. The validation coverage MUST accept the required admin service and reject an admin service with an invalid image repository or mutable reference, missing pull-only policy, a local build, published ports, literal secret values, missing readiness healthcheck, missing required configuration references, or a topology that publicly exposes internal services.

#### Scenario: Compose validation is updated with the service

- GIVEN the AdminApp Coolify Compose integration is present
- WHEN the Compose validator and its focused tests run
- THEN they MUST accept a compliant admin service and existing internal-service topology
- AND they MUST reject each prohibited admin configuration or public exposure of API, PostgreSQL, or llama.cpp

### Requirement: Focused verification coverage

The change MUST provide focused automated verification for Cloudflare token acceptance and rejection, allowlist enforcement, assertion claims, machine-proof canonicalisation and HMAC signing, forbidden-header stripping, each supported proxy operation, health behaviour, missing-configuration fail-closed behaviour, the admin Docker target build, and the Coolify Compose validator.

#### Scenario: Focused test suite

- GIVEN the change's focused unit, integration, Docker-build, and Compose-validator tests
- WHEN they are run against the BFF and deployment artifacts
- THEN they MUST demonstrate the required authentication, authorisation, proxy, header-boundary, health, configuration, container-build, and Compose-boundary behaviours

### Requirement: Coolify and Cloudflare Access runbook

The repository MUST document an operator runbook for AdminApp health checks, rollback, first administrator onboarding, Coolify FQDN configuration, and Cloudflare Access binding. It MUST provide liveness and readiness check commands, state that rollback selects a previous immutable image reference with no data migration, and describe configuring the externally supplied Cloudflare Access values and allowed subjects without prescribing their values or policy.

#### Scenario: Operator follows the runbook

- GIVEN an operator has deployed the AdminApp BFF
- WHEN the operator follows the documented health, rollback, onboarding, Coolify, and Cloudflare Access procedures
- THEN the operator MUST be able to check both health endpoints, select a previous image reference without a data migration, configure the AdminApp FQDN and Cloudflare Access binding, verify JWT validation, and complete the first-administrator setup steps

### Requirement: Deferred frontend and deployment boundary

This change MUST NOT serve an SPA, add browser end-to-end coverage, create new API endpoints, introduce an additional deployment target, or modify the API, PostgreSQL, or llama.cpp service definitions beyond validations required to preserve their internal-only boundary. The BFF MUST preserve an additive extension boundary for later SPA serving and historical-import functionality.

#### Scenario: Scope boundary review

- GIVEN the change's implementation and deployment artifacts
- WHEN they are reviewed for frontend, deployment-target, API, and internal-service expansion
- THEN no SPA-serving behaviour, browser end-to-end suite, new API endpoint, or additional deployment target MUST be present
- AND API, PostgreSQL, and llama.cpp service definitions MUST remain functionally unchanged and internal-only
