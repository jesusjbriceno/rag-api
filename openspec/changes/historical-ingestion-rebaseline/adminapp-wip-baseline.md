# AdminApp WIP Baseline (Read-only)

Captured on 09/09/2026 before any Unit 1c work. This manifest excludes the listed WIP from the historical-ingestion change. It is not an integration decision and grants no edit authority over these paths.

## Protected paths

- `Dockerfile` (modified; Admin target is pre-existing WIP)
- `Rag.sln` (modified; includes pre-existing AdminApp registration)
- `src/Rag.AdminApp.Host/` (untracked host project)
- `src/Rag.Infrastructure/AdminAuthenticationContract.cs` (untracked)
- `tests/Rag.UnitTests/AdminAppHostConfigurationTests.cs` (untracked)
- `tests/Rag.UnitTests/AdminAuthenticationContractTests.cs` (untracked)

## Host file hashes

| SHA-256 | Path |
| --- | --- |
| `8c9d218fe427d513f3b90c733d723d60d7a40c6560f50e17b714f4c974530dc3` | `src/Rag.AdminApp.Host/AdminAppHostServiceCollectionExtensions.cs` |
| `5a9309764ffc4b914587f1e5b11d263ad5f3ad073ce717b4fd323c5bcfec85cb` | `src/Rag.AdminApp.Host/AdminAppHostWebApplicationExtensions.cs` |
| `361f7e7a2360faab20d32e606b5930a55c1a6df0a9a0017579fad36726732953` | `src/Rag.AdminApp.Host/Configuration/AdminAppHostOptions.cs` |
| `ff6e70c01622d1ce757c3e83de1445aeb9b822e44a84ce6d6738102eeb905018` | `src/Rag.AdminApp.Host/Program.cs` |
| `9b67b17fd516c64170a80f1ca38c66041b1bf3a7e631f90d42b7710f67842530` | `src/Rag.AdminApp.Host/Rag.AdminApp.Host.csproj` |

## Disposition

Current disposition remains **preserve and isolate**. A separate evidence-backed assessment must decide whether and how to integrate it after the historical loader's direct-auth boundary is proven. No historical-ingestion unit may modify these paths.
