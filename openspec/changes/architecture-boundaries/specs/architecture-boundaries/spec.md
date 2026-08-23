# Architecture Boundaries Specification

## Purpose

Define seven boundaries and the accepted baseline.

## Accepted Architecture Baseline

D-003 PostgreSQL/pgvector, D-004 .NET/ASP.NET Core, D-005 modular monolith, and D-006 local background worker are Accepted for the first release.

## Requirements

### Requirement: Boundary Responsibilities

The system MUST separate API, Application, Domain, Processing, Embeddings, Persistence, and immutable Content Store. API MUST NOT contain RAG logic; Domain MUST NOT depend on HTTP or storage.

#### Scenario: Use case crosses boundaries

- GIVEN a client request
- WHEN API accepts it
- THEN Application applies Domain rules

#### Scenario: Prohibited dependency

- GIVEN a Domain rule
- WHEN it is evaluated
- THEN it remains independent of HTTP and storage

### Requirement: Dependency Direction and Accepted Baseline

API SHALL invoke Application; technical boundaries MAY use Domain contracts, but Domain MUST NOT depend on them. The first release MUST be a .NET/ASP.NET Core modular monolith backed by PostgreSQL/pgvector with a local worker for long-running ingestion. It MUST NOT require microservices, distributed transactions, or an external broker.

#### Scenario: First-release baseline

- GIVEN the first release
- WHEN its architecture is evaluated
- THEN it uses the D-003 through D-006 accepted baseline
- AND long-running ingestion has a local worker boundary

#### Scenario: Future extraction need

- GIVEN an independent need emerges
- WHEN extraction is evaluated
- THEN responsibilities and dependencies remain explicit
