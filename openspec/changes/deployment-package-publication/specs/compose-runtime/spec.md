# compose-runtime Specification

## Purpose

Run production Compose from published images rather than local image builds.

## Requirements

### Requirement: Immutable Production Image Selection

Production Compose MUST reference `ghcr.io/jesusjbriceno/rag-api` with `RAG_API_IMAGE_TAG` set to one specific published immutable version tag. It MUST NOT use `latest`, an empty tag, or a floating channel tag, and it MUST NOT declare an application `build:` directive.

#### Scenario: Pinned production runtime
- GIVEN `RAG_API_IMAGE_TAG` names a published immutable version
- WHEN production Compose is evaluated
- THEN it references that exact GHCR image without `build:`

#### Scenario: Unsafe image tag
- GIVEN the production tag is `latest`, empty, or floating
- WHEN production Compose is evaluated
- THEN validation fails before the application starts

### Requirement: Pull-only Production Startup

Production Compose MUST pull its referenced application image and MUST NOT fall back to a local build. The development Compose override MAY retain local builds for developer iteration.

#### Scenario: Available published image
- GIVEN the pinned GHCR image is accessible
- WHEN production Compose starts
- THEN it pulls and runs that image without building

#### Scenario: Unavailable published image
- GIVEN the pinned GHCR image cannot be pulled
- WHEN production Compose starts
- THEN startup fails without a local-build fallback
