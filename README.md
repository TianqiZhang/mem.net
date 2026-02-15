# mem.net

`mem.net` is a configurable, Azure-oriented memory service for multi-agent systems.

It provides:
- Profile-driven memory documents (no hard-coded memory types)
- Optimistic concurrency with ETags
- Idempotent writes
- Context assembly for orchestrators
- Event digest write/search
- Replay-ready update contracts with evidence and confidence

## Repository Layout
- `MEMORY_SERVICE_SPEC.md`: detailed technical spec
- `src/MemNet.MemoryService`: ASP.NET Core service
- `tests/MemNet.MemoryService.SpecTests`: executable spec tests (dependency-free)
- `TASK_BOARD.md`: implementation plan and progress
- `AGENTS.md`: contributor/agent operating guide

## Tech Stack
- .NET 8
- ASP.NET Core Minimal API
- Local filesystem storage provider (Blob-compatible abstraction)
- In-memory event search provider for v1

## Quick Start
1. Build
```bash
dotnet build src/MemNet.MemoryService/MemNet.MemoryService.csproj --no-restore -p:ResolvePackageAssets=false
dotnet build tests/MemNet.MemoryService.SpecTests/MemNet.MemoryService.SpecTests.csproj --no-restore -p:ResolvePackageAssets=false
```

2. Run service
```bash
dotnet run --project src/MemNet.MemoryService
```

3. Run spec tests
```bash
dotnet tests/MemNet.MemoryService.SpecTests/bin/Debug/net8.0/MemNet.MemoryService.SpecTests.dll
```

## Configuration
Service behavior is driven by profiles and schemas loaded from `config/`:
- schema registry
- profile registry
- path/write/retention/confidence/compaction rules

## Notes
- `NuGet.Config` clears external feeds so this repo can build in restricted/offline environments.
- v1 implementation focuses on core acceptance criteria from the spec.
