# package-publication Specification

## Purpose

Publish traceable, signed Docker images only after security validation.

## Requirements

### Requirement: Secure Publication Order

For an eligible candidate, the system MUST build the image, generate its SBOM and provenance, scan it, sign it, and then publish it in that order. HIGH or CRITICAL vulnerabilities MUST block signing and publication.

#### Scenario: Clean candidate publication
- GIVEN an eligible image has no HIGH or CRITICAL findings
- WHEN its scan succeeds
- THEN the signed image, SBOM, and provenance are published

#### Scenario: Blocking vulnerability
- GIVEN an eligible image has a HIGH or CRITICAL finding
- WHEN its scan completes
- THEN signing and publication do not occur

#### Scenario: Non-blocking finding
- GIVEN an eligible image has only LOW or MEDIUM findings
- WHEN its scan completes
- THEN publication may continue under the defined policy

### Requirement: Verifiable Immutable Identity

The system MUST use keyless OIDC signing and associate the signature, SBOM, and provenance with the published image digest. It MUST expose the exact publication version in image and application metadata. Verified `develop` builds MUST publish a `develop-{sha}` pre-release tag. A semver release tag MUST publish its exact version tag; an existing tag MUST NOT be moved to a different digest.

#### Scenario: Pre-release promotion
- GIVEN a verified `develop` commit with SHA `abc123`
- WHEN its image is published
- THEN it is available as `develop-abc123` with matching verifiable metadata

#### Scenario: Tag collision
- GIVEN a version tag already identifies a different digest
- WHEN publication requests that tag
- THEN publication fails without replacing the existing tag
