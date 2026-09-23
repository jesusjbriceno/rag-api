# SDD Project Context — rag-api

## Initialization

- Project: `rag-api`
- Root: `/opt/wf/rag-api`
- Artifact store: OpenSpec
- Execution: automatic
- Delivery strategy: `ask-on-risk`
- Review budget: 400 changed lines
- Skill resolution: `paths-injected`

## Repository and stack

- .NET SDK: 10.0.111, pinned by `global.json`
- ASP.NET Core / C# solution: `Rag.sln`
- Projects: API, Application, Domain, Infrastructure, AdminApp, Companion, Operator
- Tests: `Rag.UnitTests`, `Rag.IntegrationTests`, `Rag.Companion.Tests`
- Data/runtime: PostgreSQL with pgvector, Npgsql, EF Core, Docker Compose, hosted workers, llama.cpp embedding runtime
- Documentation and architectural baseline: `.proposals/*.md`, `README.md`

## Validation commands

- Restore: `dotnet restore Rag.sln`
- Release build: `dotnet build Rag.sln --configuration Release --no-restore`
- Full tests: `dotnet test Rag.sln --configuration Release --no-build`
- Unit tests: `dotnet test tests/Rag.UnitTests/Rag.UnitTests.csproj --configuration Release`
- Integration tests: `dotnet test tests/Rag.IntegrationTests/Rag.IntegrationTests.csproj --configuration Release`
- Companion tests: `dotnet test tests/Rag.Companion.Tests/Rag.Companion.Tests.csproj --configuration Release`
- Coverage: `dotnet test Rag.sln --configuration Release --no-build --collect:"XPlat Code Coverage;Format=opencover" --results-directory ./TestResults/coverage`
- Compose validation: `bash scripts/validate-coolify-compose.sh` and `python3 scripts/test-validate-coolify-compose.py`
- Publication guard tests: `bash scripts/test-ci-publication-guards.sh`, `bash scripts/test-ci-publication-marker.sh`, `bash scripts/test-ci-publication-verifier.sh`, and `bash scripts/test-ci-multiarch-index.sh`
- Workflow lint: `actionlint .github/workflows/*.yml` when actionlint is installed

## TDD assessment

Strict TDD is viable and enabled: a .NET SDK, solution, test projects, and CI test commands are present. Apply and verify phases must use RED/GREEN/TRIANGULATE/REFACTOR and record test evidence. No test execution was performed during initialization.

## Conventions and risks

- Technical artifacts are written in English.
- Preserve consumer-agnostic/public-repository constraints and deferred decisions.
- Do not implement product changes during initialization.
- Existing working-tree changes in `.atl/skill-registry.md`, `.gitignore`, and `.engram/` predate this initialization and are outside its scope.
- `.atl/skill-registry.md` exists and is available for delegation skill resolution.
