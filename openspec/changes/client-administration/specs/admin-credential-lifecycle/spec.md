# Admin Credential Lifecycle Specification

## Purpose

Issue and govern unique credentials for integration-client instances without disclosing durable secret material.

## Requirements

### Requirement: Credential Issue and Secret-Once Delivery

`POST /api/v1/admin/clients/{id}/credentials` MUST issue a unique credential for an existing integration client. Each credential MUST be unique to its integration or instance and MAY have an optional expiry; a supplied expiry MUST be future-dated. Plaintext secret material MUST be returned only by successful issue and rotate responses, exactly once, and MUST NOT be recoverable afterward.

#### Scenario: Issue an optional-expiry credential

- GIVEN an authorized request for an existing client with no expiry or a future expiry
- WHEN it issues a credential
- THEN it returns the new credential metadata and secret once and appends an audit event

#### Scenario: Invalid issue is safe

- GIVEN an unknown client or an expiry at or before issuance time
- WHEN the issue endpoint is called
- THEN it fails without creating a credential or exposing secret material

### Requirement: Metadata Inspection Without Secret Disclosure

`GET /api/v1/admin/clients/{id}/credentials` and `GET /api/v1/admin/credentials/{id}` MUST expose credential metadata and lifecycle status only. Listing, detail, audit, application logs, and failure responses MUST NOT expose plaintext secret, hash, salt, authorization header, token, assertion, credential value, or credential-bearing body.

#### Scenario: Inspect credential metadata

- GIVEN an existing credential and dual-authenticated request
- WHEN it lists or inspects the credential
- THEN it receives metadata, status, and optional expiry without secret material

#### Scenario: Secret is unavailable after delivery

- GIVEN an issued credential whose issue response is no longer available
- WHEN any list or inspect endpoint is called
- THEN no endpoint can retrieve its secret material

### Requirement: Rotation, Revocation, and Concurrency

`POST /api/v1/admin/credentials/{id}/rotate` MUST replace an active credential's secret and invalidate the prior secret; `POST /api/v1/admin/credentials/{id}/revoke` MUST make the credential unusable immediately for future exchange and protected access validation. Both operations MUST be conditional on current credential version. Concurrent or replayed mutations MUST produce one durable outcome; stale or conflicting operations MUST return a safe conflict result and MUST NOT emit an additional secret or audit mutation.

#### Scenario: Rotation invalidates the prior secret

- GIVEN an active credential
- WHEN an authorized request rotates it
- THEN the new secret is delivered once and the prior secret no longer exchanges for a JWT

#### Scenario: Concurrent revoke and rotate conflict safely

- GIVEN rotation and revocation race on the same credential version
- WHEN both requests are processed
- THEN at most one state transition succeeds and the loser receives a conflict without secret disclosure

### Requirement: Credential Lifecycle Test Coverage

Authorization, no-secret-response, expiry, immediate-revocation, idempotency, and concurrent mutation tests MUST cover all credential endpoints. Existing key-id/secret JWT exchange for active, unexpired service-client credentials MUST remain compatible.

#### Scenario: Expired credential is denied

- GIVEN a credential whose optional expiry has passed
- WHEN its key-id and secret are exchanged
- THEN the exchange is rejected while unrelated active credentials remain usable
