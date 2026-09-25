# Admin Audit Trail Specification

## Purpose

Provide a durable, queryable record of administrative lifecycle activity without retaining secrets or conflating person and application identity.

## Requirements

### Requirement: Append-Only Verified Attribution

Each completed administrative mutation MUST append an immutable audit event containing a verified human `ActorSubject`, a distinct trusted-app identifier, action, outcome, occurrence time, and applicable target identifiers. `ActorSubject` MUST derive only from the verified internal assertion, not from a credential or raw header. Failed authorization and validation MAY be recorded only with safe, non-secret fields. Audit records MUST NOT be updated or deleted except under the configured retention policy.

#### Scenario: Mutation is attributed to a person and app

- GIVEN a dual-authenticated operator creates, issues, rotates, or revokes
- WHEN the mutation completes
- THEN one append-only event records its verified subject and distinct app identifier

#### Scenario: Credential identity cannot impersonate an actor

- GIVEN a request supplies only a service credential or proxy identity header
- WHEN it attempts an administrative mutation
- THEN no successful audit event is created with that value as `ActorSubject`

### Requirement: Secret-Safe Audit Content and Logging

Audit persistence, telemetry, logs, and audit responses MUST NOT contain plaintext secrets, hashes, salts, headers, tokens, assertions, credentials, or credential-bearing request or response bodies. Audit metadata MUST be limited to an allowlisted, non-secret schema; error handling MUST preserve the same prohibition.

#### Scenario: Audit read redacts sensitive material

- GIVEN credential issue or rotation completed
- WHEN an authorized operator reads its audit event
- THEN the event identifies the action and targets without any secret-bearing field

#### Scenario: Failure logging remains safe

- GIVEN a malformed request contains credential-bearing content
- WHEN validation rejects it
- THEN returned errors and emitted logs omit that content

### Requirement: Paginated Audit Read and Retention Contract

`GET /api/v1/admin/audit` MUST return audit events in stable chronological order using an opaque cursor and bounded page size. A cursor beyond the final event MUST return an empty page; an invalid cursor or size MUST return validation failure. Deployment configuration MUST explicitly select a positive retention duration or indefinite retention; invalid or absent retention configuration MUST prevent the admin plane from operating. Retention implementation and storage mechanics remain design decisions, but pruning MUST be auditable and MUST NOT expose secrets.

#### Scenario: Boundary pagination is deterministic

- GIVEN audit events spanning more than one page
- WHEN an operator follows each returned cursor through the final page
- THEN every event is returned once in stable order and the next page is empty

#### Scenario: Invalid pagination is rejected

- GIVEN an invalid cursor or out-of-range page size
- WHEN an operator requests the audit endpoint
- THEN it receives a non-sensitive validation failure without data disclosure

### Requirement: Audit Preservation Tests

Migration and integration tests MUST prove existing service-client exchange data remains readable, audit events persist across restart, audit writes are append-only, and prohibited fields are absent from persisted events, logs, and API responses.

#### Scenario: Existing data and new audit coexist

- GIVEN pre-existing service-client and credential records
- WHEN compatible admin and audit persistence is deployed
- THEN exchange remains functional and subsequent admin actions produce queryable audit events
