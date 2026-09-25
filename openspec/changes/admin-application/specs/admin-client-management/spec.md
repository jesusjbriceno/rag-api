# Admin Client Management Specification

## Purpose

Manage clients through supported API operations.

## Requirements

### Requirement: Create and inspect clients

The UI MUST let administrators create, list, and view clients. It MUST NOT offer update or archive operations.

#### Scenario: Administrator creates and finds a client

- GIVEN valid client input
- WHEN an administrator creates and lists clients
- THEN the created client SHALL be listed with details available

#### Scenario: Unsupported lifecycle action is absent

- GIVEN a displayed client detail
- WHEN the UI renders actions
- THEN no update or archive action MUST be available
