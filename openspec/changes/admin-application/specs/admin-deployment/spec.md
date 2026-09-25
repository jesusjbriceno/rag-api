# Admin Deployment Specification

## Purpose

Package the application for private Coolify deployment.

## Requirements

### Requirement: Private Coolify deployment

The project MUST provide an admin container target, Coolify Compose service, and CI coverage. The service MUST NOT be publicly exposed outside its Cloudflare-protected entry point.

#### Scenario: Deployment artifacts are built

- GIVEN CI runs for admin changes
- WHEN admin artifacts are built
- THEN container and UI checks SHALL succeed

#### Scenario: Service exposure is reviewed

- GIVEN the Coolify Compose service
- WHEN its exposure is inspected
- THEN it MUST not declare unintended public exposure

### Requirement: Current deployment documentation

Deployment documentation MUST use Coolify and MUST NOT retain Dokploy references.

#### Scenario: Documentation is searched

- GIVEN deployment documentation
- WHEN terminology is reviewed
- THEN Coolify SHALL appear and Dokploy MUST be absent
