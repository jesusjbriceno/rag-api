# Admin Audit View Specification

## Purpose

Provide paginated audit visibility.

## Requirements

### Requirement: Paginated audit viewing

The UI MUST display existing API audit events and SHALL navigate available pages.

#### Scenario: Administrator navigates audit pages

- GIVEN audit events span pages
- WHEN an administrator selects a later page
- THEN the UI SHALL show that page and pagination state

#### Scenario: Audit page has no events

- GIVEN an empty selected page
- WHEN its response is displayed
- THEN the UI MUST show an empty state without inventing events
