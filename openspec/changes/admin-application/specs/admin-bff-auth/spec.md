# Admin BFF Auth Specification

## Purpose

Provide a trusted administration boundary.

## Requirements

### Requirement: Restricted BFF access and proxying

The BFF MUST validate Cloudflare Access, allow only configured administrator identities, issue and sign server-side API requests, and serve the SPA. Browsers MUST NOT receive assertion private keys or API HMAC secrets.

#### Scenario: Allowed administrator uses the application

- GIVEN an allowlisted Cloudflare identity
- WHEN it requests an administrative operation
- THEN the BFF SHALL proxy the corresponding existing admin API operation

#### Scenario: Unallowed or invalid identity is rejected

- GIVEN a missing, invalid, or unallowlisted identity
- WHEN it requests the BFF
- THEN the BFF MUST deny access without issuing an assertion or exposing a secret
