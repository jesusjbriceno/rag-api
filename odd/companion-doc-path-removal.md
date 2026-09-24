# ODD Tasks — Drop the companion's LibreOffice DOC path

**Goal:** make the code match the decision already recorded — legacy `.doc` stays out of scope — by removing
the companion's DOC conversion path and the LibreOffice provisioning from both workflows.

**Why:** `openspec/changes/historical-ingestion-rebaseline/apply-progress.md:3137` records it: legacy DOC is
intentionally unsupported, *"the user explicitly decided not to require client-side third-party software for DOC
extraction; users with legacy DOC files should convert them to DOCX before running real extraction"*, and the
operational-cost conclusion followed from a real attempt: even with LibreOffice wired, the sample `.doc` still
failed conversion (`soffice.com` exit code 1). The sample is 4 DOCX / 12 MD / 14 PDF / **0 DOC**. The engine
policy already refuses the format (`doc_libreoffice_required`, and `CandidateClassifier` maps `.doc` to
`unsupported_format`). What is left is the companion half plus the CI provisioning: unfinished halves of a
decision, not a new decision.

**Correction measured before acting (do not skip this):** `ci-pr` provisions the MSI and nothing uses it — every
test that touches `LibreOfficeRunner` is `[LinuxFact]` against a fake POSIX `soffice` shim, so on Windows they are
skipped and the version check only proves the installer installed. `ci-release` **does** use it: the
`companion-format-smoke` job generates a real `.doc` from the DOCX fixture with
`soffice --headless --convert-to doc` and asserts all five formats extract and ingest. But `ci-release` has
**never run** — 0 runs, 0 published releases, and its only tag `v0.1.0-rc.1` was pushed before the workflow file
existed — and it could not reach that smoke anyway, because the `companion` job it depends on pins
`CycloneDX --version 3.1.0`, a version that does not exist on NuGet (proved by `e44e12a` for `ci-pr`). So the DOC
path's only real verification is unreachable, and the decision stands on the corrected facts.

**Considered and rejected — MarkItDown.** It has no legacy `.doc` converter: the package ships
`_docx_converter.py`, `_xlsx_converter.py` and `_doc_intel_converter.py`, and no `_doc_converter.py`; its optional
extras include `[xls]` for older Excel and nothing equivalent for older Word. It also requires Python
`>=3.10,<3.15` plus pip/venv (base deps: beautifulsoup4, requests, markdownify, magika, charset-normalizer,
defusedxml), which is *more* machinery on the operator machine than a pinned MSI inside a self-contained
single-file `win-x64` CLI, and swapping the extractor would void the measured evidence (Unit 5 `adapt`
disposition, 71,580 observations, 35,788 docs/h, median 1.75 ms).

## Tasks

- [x] 1. Delete the companion's DOC path and rewire the registry, host and config.
  Evidence: `git rm` removed `DocAdapter.cs` and `LibreOffice/{ILibreOfficeRunner,LibreOfficeException,LibreOfficeRunner}.cs`
  (the `LibreOffice/` directory is gone); `CreateDefault()` lost its `LibreOfficeRunner` parameter and its
  `[".doc"]` entry; `CommandLine.cs` now calls `AdapterRegistry.CreateDefault()`; `CompanionConfig` no longer
  declares `LibreOfficePath`.
- [x] 2. Delete the tests that only existed to drive it, and fix the tests that referenced it.
  Evidence: `git rm` removed `DocAdapterTests.cs`, `LibreOfficeRunnerTests.cs` and `LinuxFactAttribute.cs`;
  `AdapterRegistryTests` now keeps the boundary honest via `CreateDefault_does_not_register_legacy_doc_adapter`
  (asserts `.doc` extraction yields `ExtractionFailed`, i.e. not registered); `CompanionRunTests` calls
  `CreateDefault()`.
- [x] 3. Update the companion documentation and its documentation test.
  Evidence: `docs/historical-import-companion.md` drops the LibreOffice prerequisite row, the "separate
  prerequisite" sentence, the install step, the `libreOfficePath` config row and the LibreOffice notice, and now
  lists four formats (`docx`, `md`, `pdf`, `txt`); all required headings are intact. `CompanionDocsTests` drops
  only the `LibreOffice` assertion and keeps the section assertions.
- [x] 4. Remove the LibreOffice steps and notices from `ci-pr.yml`, and from `ci-release.yml` turn the smoke into
  a four-format smoke.
  Evidence: both `Provision LibreOffice 26.8 x64` / `Verify LibreOffice version` steps and the LibreOffice notices
  line removed from `ci-pr.yml` and from the `companion` job of `ci-release.yml`; `companion-format-smoke` renamed
  to "Clean-machine 4-format smoke", its step renamed, the `soffice` block and `libreOfficePath` entry removed, and
  the pass/fail messages now name four formats. Both workflows parse as valid YAML (actionlint is not installed).
- [ ] 5. Fix the `CycloneDX` pin in `ci-release.yml` as its own commit, because that pin blocks the whole release
  workflow independently of this change. Reason: the pin is changed to `6.2.0` in the working tree, but its separate
  commit is parent-owned (no commits were made here).
- [x] 6. Build, run the suite, and confirm no reference to the removed path survives.
  Evidence: `dotnet build Rag.sln --configuration Release` → 0 errors (8 pre-existing `NU1903` warnings, none new);
  `dotnet test Rag.sln --configuration Release` → all five projects green (90 + 124 + 30 + 425 + 60 = 729 passed,
  0 failed, 0 skipped); residue grep shows only deliberate keeps (engine policy/vocabulary, fixture manifest,
  OpenSpec/historical evidence records, and this plan file).

## Exact surface

**Delete:** `src/Rag.Companion/Adapters/DocAdapter.cs`; `src/Rag.Companion/LibreOffice/` (`ILibreOfficeRunner.cs`,
`LibreOfficeException.cs`, `LibreOfficeRunner.cs` — the directory goes too);
`tests/Rag.Companion.Tests/DocAdapterTests.cs`; `tests/Rag.Companion.Tests/LibreOfficeRunnerTests.cs`;
`tests/Rag.Companion.Tests/LinuxFactAttribute.cs` (its only consumer is the deleted runner test).

**Modify:** `src/Rag.Companion/Adapters/AdapterRegistry.cs` (`CreateDefault()` loses its `LibreOfficeRunner`
parameter and its `[".doc"]` entry); `src/Rag.Companion/Host/CommandLine.cs`;
`src/Rag.Companion/Host/CompanionConfig.cs` (drop `LibreOfficePath`);
`tests/Rag.Companion.Tests/AdapterRegistryTests.cs`; `tests/Rag.Companion.Tests/CompanionRunTests.cs`;
`tests/Rag.Companion.Tests/CompanionDocsTests.cs`; `docs/historical-import-companion.md`;
`.github/workflows/ci-pr.yml`; `.github/workflows/ci-release.yml`.

**Keep, deliberately:**

- The engine's policy and vocabulary: `Rag.HistoricalLoader.Core/Extraction/ExtractionResult.cs`,
  `Benchmark/LocalExtraction.cs` (`unavailable_libreoffice_absent`, `RequiresLibreOffice`),
  `Engine/Extraction/CompanionReflectionExtractor.cs` and `Engine/ReflectionCompanionExtractor.cs`. These are the
  machine-readable *reason* the format is refused, they are covered by existing tests, and deleting them would
  erase why a `.doc` is reported unsupported.
- `tests/fixtures/import/sample.doc` and its `manifest.json` entry: the engine still classifies a `.doc` as
  `unsupported_format`, so the fixture keeps its meaning.
- Every historical record: `docs/historical-ingestion-rebaseline/CompanionDisposition.md`,
  `openspec/changes/historical-ingestion-rebaseline/**`, `openspec/changes/historical-import-companion/**` and the
  archived change. They are evidence, not current capability docs, and must not be rewritten.
- `openspec/changes/historical-ingestion-rebaseline/tasks.md`: its LibreOffice follow-up gets ticked after the PR's
  CI is green, not inside this change.
