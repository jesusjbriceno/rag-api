# Admin Credential Management Specification

## Purpose

Manage credentials without retaining generated secrets.

## Requirements

### Requirement: Credential operations and secret confirmation

The UI MUST issue, list, detail, rotate, and revoke credentials through existing operations. Issued or rotated secrets MUST appear only once, and their view MUST require explicit confirmation before dismissal.

#### Scenario: Administrator rotates a credential

- GIVEN an active credential
- WHEN an administrator rotates it
- THEN the UI SHALL show its one-time secret view

#### Scenario: Unconfirmed secret cannot be dismissed

- GIVEN an unconfirmed one-time secret
- WHEN an administrator attempts dismissal
- THEN the UI MUST block dismissal until explicit confirmation
