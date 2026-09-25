# Admin Historical Import Specification

## Purpose

Observe a historical snapshot import from a future local companion.

## Requirements

### Requirement: Companion-mediated snapshot import

The UI MUST request and show a companion-supplied snapshot job's progress and result. Browsers MUST NOT access local files or offer Dropbox, scanning, scheduling, sync, deletion propagation, OAuth, or remote browsing.

#### Scenario: Companion job progress is shown

- GIVEN a companion accepts a snapshot job
- WHEN an administrator views it
- THEN the UI SHALL show its reported progress and final result

#### Scenario: Companion is unavailable

- GIVEN no companion can accept the request
- WHEN an administrator requests an import
- THEN the UI MUST report unavailable status and MUST NOT inspect local folders
