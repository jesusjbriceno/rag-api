# Companion Disposition — Historical Ingestion Rebaseline

> Change: `historical-ingestion-rebaseline` — Unit 5 (Companion disposition record and extractor interface).

## Outcome

Outcome: adapt

Exactly one disposition is chosen. The Companion reflection adapter demonstrated reliable
sustained extraction behavior, but its sequential/BFF-coupled host and reflection/string dispatch
contract preclude clean reuse as the core extractor. The decision is therefore **adapt**, not
`reuse` and not `reference only`.

## Evidence cited

| Item | Value |
| --- | --- |
| Manifest | `b11671f8-0c60-42bf-9f1b-f6f2bf27d759` — 68 total (58 eligible / 10 unsupported) |
| Sample set / report key | `ea6a4046-6873-9b8e-5fa2-016d45779539`, seed `20260910` |
| Sample members | 30 (4 DOCX / 12 MD / 14 PDF / 0 DOC) |
| Sustained observations | 71,580, zero errors/timeouts/hangs, sustained reliability satisfied |
| Throughput | 35,788.91 docs/h · 12.1437 source GB/h · 0.0973 normalized GB/h |
| Extraction latency | median 1.7535 ms · p95 87.8025 ms · max 940.5117 ms |

## Rationale

The 2-hour sustained run across the 30-member sample completed with zero errors, timeouts, or hangs,
which passes the reliability bar for reuse. However, the adapter is:

- coupled to the Companion BFF host and its sequential invocation model, and
- dispatched through a reflection/string contract (adapter type names and method names as strings),
  which does not isolate cleanly from BFF hosting concerns.

Adapting the observed, reliable extraction behavior into an isolated core extractor behind the
`IExtractor` abstraction is safer than reusing the coupled host directly, and the behavior evidence
is too strong to discard as `reference only`.

## Limitations

- The sustained run loops the 30-member sample; no full-corpus claim is made.
- DOC is unsupported (`doc_libreoffice_required`) — LibreOffice is required but unavailable in the
  reflection boundary.
- Report durability failed on the first attempt (35,208 observations) before a retry persisted the
  full 71,580 observations.
- Resource counters require careful scope (benchmark process vs. child process attribution).
- No full-corpus throughput/capacity claim is made.

## Boundary

- `IExtractor` (`Rag.HistoricalLoader.Core.Extraction`) is the only extraction abstraction the
  engine depends on.
- The engine must not project-reference `Rag.Companion`; the WPF shell has no transitive Companion
  reference through the engine contract.
- This `adapt` disposition authorizes a bounded follow-up to `src/Rag.Companion/` that must be its
  own future change and its own review (per the Unit 5 decision gate). No production engine
  references Companion before that gate.
