# Admin Client Lifecycle Specification

## Purpose

Manage integration-client identities through the internal administrative plane while preserving existing service-client behavior.

## Scope Boundary

Collection owner assignment and multi-client or shared-collection authorization are out of scope; this capability defines no such endpoint, data change, or migration behavior.

## Requirements

### Requirement: Integration Client Creation

`POST /api/v1/admin/clients` MUST create one integration client for one named integration or instance after dual authentication. Client identity MUST be unique, name validation MUST reject blank or overlong values, and duplicate creation MUST not create a second client. Creation returns metadata only; it MUST NOT return credential secret material. Credential issuance is performed only by the credential-issue endpoint.

#### Scenario: Create a unique integration client

- GIVEN an authorized request with a valid unique integration name
- WHEN it calls `POST /api/v1/admin/clients`
- THEN it receives the created client metadata and an audit event is appended

#### Scenario: Duplicate or invalid client is rejected

- GIVEN a blank, overlong, or already-used client name
- WHEN the creation endpoint is called
- THEN it returns a validation or conflict failure with no client or credential created

### Requirement: Client Discovery Without Secrets

`GET /api/v1/admin/clients` and `GET /api/v1/admin/clients/{id}` MUST return only authorized integration-client metadata. Detail MAY include credential metadata but MUST NOT include plaintext secrets, hashes, salts, headers, tokens, assertions, credentials, or credential-bearing bodies. A missing client MUST return a non-sensitive not-found result.

#### Scenario: Authorized discovery returns metadata

- GIVEN existing integration clients and a dual-authenticated request
- WHEN it lists clients or reads one existing client
- THEN the response contains the requested metadata and no secret material

#### Scenario: Unknown client is isolated

- GIVEN a valid administrative request for a non-existent client identifier
- WHEN it requests client detail
- THEN it receives not found without credential or audit data disclosure

### Requirement: Compatibility and Mutation Safety

Client lifecycle MUST remain additive: existing `ServiceClient` identities, service-client JWT exchange, credential ownership, and collection ownership enforcement MUST retain their current behavior. Any migration MUST preserve existing client records and references and MUST NOT alter collection-owner assignment or shared-collection authorization. A repeated mutating request MUST be protected by an operation idempotency contract that prevents duplicate client creation and does not persist credential-bearing request content.

#### Scenario: Existing client remains usable after migration

- GIVEN a service client and credential created before the admin capability
- WHEN compatible schema changes are applied
- THEN its existing token exchange and collection access behavior remain unchanged

#### Scenario: Repeated create does not duplicate state

- GIVEN a completed create operation is retried with its operation identifier
- WHEN the API receives the retry
- THEN it does not create another client and returns a safe duplicate outcome
