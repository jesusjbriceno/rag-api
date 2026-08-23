# Product Decisions Specification

## Purpose

Record resolved product behavior within the accepted baseline.

## Accepted Architecture Baseline

D-003 PostgreSQL/pgvector, D-004 .NET/ASP.NET Core, D-005 modular monolith, and D-006 local worker are Accepted; they do not alter the behavior below.

## Requirements

### Requirement: Token Exchange Authentication

The system MUST exchange KeyId and secret for a short-lived token. Protected requests MUST use verified identity, not caller input. Credentials MUST support issuance, revocation, rotation, and expiry.

#### Scenario: Valid exchange

- GIVEN active credentials
- WHEN the client exchanges them
- THEN it receives a short-lived token

#### Scenario: Caller-supplied identity

- GIVEN caller input names a client
- WHEN credentials identify another
- THEN verified identity is used

### Requirement: Content Deduplication

The system MUST return the existing document/version for equal external reference and hash; it MUST create a new DocumentVersion for a changed hash, and a new Document without an external reference, including across collections.

#### Scenario: Idempotent referenced content

- GIVEN matching reference and hash
- WHEN content is resubmitted
- THEN its document/version is returned

#### Scenario: Unreferenced duplicate

- GIVEN identical unreferenced content
- WHEN it is submitted
- THEN a new document is created

### Requirement: Cross-Collection Compatibility

The system MUST validate profiles before multi-collection search and reject incompatible profiles.

#### Scenario: Compatible collections

- GIVEN compatible profiles
- WHEN cross-collection search is requested
- THEN it proceeds

#### Scenario: Incompatible collections

- GIVEN incompatible profiles
- WHEN cross-collection search is requested
- THEN it is rejected

## Deferred Decisions and Non-goals

HTTP errors, token format/headers, hashing, scopes, idempotency, ContentReference, retry identity, worker claim/work selection, deletion, metadata, filters, indexing, reranking, hybrid search, multi-tenancy, external queue/broker, OCR/vision, SDK generation, and production Content Store remain deferred. They SHALL be resolved by the relevant contract, security, persistence, scale, or product-need phase; implementation is out of scope.
