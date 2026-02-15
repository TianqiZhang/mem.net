# mem.net

`mem.net` is a configurable, Azure-oriented memory service for multi-agent systems.

It provides:
- Profile-driven memory documents (no hard-coded memory types)
- Optimistic concurrency with ETags
- Idempotent writes
- Context assembly for orchestrators
- Event digest write/search
- Replay-ready update contracts with evidence and confidence
- Provider selection (`filesystem` default, `azure` available with Azure SDK build flag)

## Repository Layout
- `MEMORY_SERVICE_SPEC.md`: detailed technical spec
- `src/MemNet.MemoryService`: ASP.NET Core service
- `tests/MemNet.MemoryService.SpecTests`: executable spec tests (dependency-free)
- `TASK_BOARD.md`: implementation plan and progress
- `AGENTS.md`: contributor/agent operating guide

## Tech Stack
- .NET 8
- ASP.NET Core Minimal API
- Local filesystem storage provider
- Azure Blob + Azure AI Search provider (build-time enabled)

## Quick Start
1. Restore and build (default filesystem provider)
```bash
dotnet restore MemNet.sln --configfile NuGet.Config
dotnet build MemNet.sln -c Debug
```

2. Run service
```bash
dotnet run --project src/MemNet.MemoryService
```

3. Run spec tests
```bash
dotnet tests/MemNet.MemoryService.SpecTests/bin/Debug/net8.0/MemNet.MemoryService.SpecTests.dll
```

4. Optional: override roots/provider at runtime
```bash
MEMNET_DATA_ROOT=/tmp/memnet-data \
MEMNET_CONFIG_ROOT=/Users/tianqi/code/mem.net/src/MemNet.MemoryService/config \
MEMNET_PROVIDER=filesystem \
dotnet run --project src/MemNet.MemoryService
```

5. Enable Azure SDK provider build (requires NuGet access)
```bash
dotnet build src/MemNet.MemoryService/MemNet.MemoryService.csproj -p:MemNetEnableAzureSdk=true
```

6. Run with Azure provider
```bash
MEMNET_PROVIDER=azure \
MEMNET_AZURE_STORAGE_SERVICE_URI="https://<account>.blob.core.windows.net" \
MEMNET_AZURE_EVENTS_CONTAINER="memnet-events" \
MEMNET_AZURE_DOCUMENTS_CONTAINER="memnet-documents" \
MEMNET_AZURE_AUDIT_CONTAINER="memnet-audit" \
MEMNET_AZURE_SEARCH_ENDPOINT="https://<service>.search.windows.net" \
MEMNET_AZURE_SEARCH_INDEX="<events-index>" \
MEMNET_AZURE_RETRY_MAX_RETRIES=3 \
MEMNET_AZURE_RETRY_DELAY_MS=200 \
MEMNET_AZURE_RETRY_MAX_DELAY_MS=2000 \
MEMNET_AZURE_NETWORK_TIMEOUT_SECONDS=30 \
dotnet run --project src/MemNet.MemoryService -p:MemNetEnableAzureSdk=true
```

## Configuration
Service behavior is driven by profiles and schemas loaded from `config/`:
- schema registry
- profile registry
- path/write/retention/confidence/compaction rules
- provider can be selected via `MemNet:Provider` or `MEMNET_PROVIDER`
- Azure settings can be provided via `MemNet:Azure:*` or `MEMNET_AZURE_*` environment variables

Azure provider startup validation enforces:
- `StorageServiceUri` must be present and absolute.
- Search settings must be configured as a pair (`SearchEndpoint` + `SearchIndexName`) or both omitted.
- Retry/timeout settings must stay within bounded numeric ranges.

## Azure Search Index Contract
When `MemNet:Azure:SearchEndpoint` and `MemNet:Azure:SearchIndexName` are set, event digests are indexed with these fields:
- `id` (key, string; deterministic hash of tenant/user/event)
- `event_id`, `tenant_id`, `user_id`, `service_id`, `source_type`, `digest`, `snapshot_uri` (string)
- `timestamp` (date-time offset)
- `keywords`, `project_ids`, `evidence_message_ids` (string collections)
- `evidence_start`, `evidence_end` (int)

## Notes
- `MemNetEnableAzureSdk` defaults to `false` to keep offline/default builds deterministic.
- Azure provider types are fully implemented, but require building with `/p:MemNetEnableAzureSdk=true`.
- Azure-enabled builds emit binaries under `src/MemNet.MemoryService/bin/Debug/azure/net8.0/` to avoid collisions with default binaries.
- v1 implementation focuses on core acceptance criteria from the spec.
- If `MEMNET_PROVIDER=azure` is used without Azure SDK build flag, endpoints return `501 AZURE_PROVIDER_NOT_ENABLED`.
