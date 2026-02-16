# mem.net

[![CI](https://github.com/TianqiZhang/mem.net/actions/workflows/ci.yml/badge.svg)](https://github.com/TianqiZhang/mem.net/actions/workflows/ci.yml)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![Provider](https://img.shields.io/badge/provider-filesystem%20%7C%20azure-0A7EA4)

Composable memory infrastructure for multi-agent systems.

`mem.net` gives agents a shared, durable memory API with optimistic concurrency, deterministic context assembly, event recall, and lifecycle cleanup.
Application-specific memory semantics (categories, slot rules, schemas) are owned by SDK/application code.

## Why mem.net

- Keep memory durable and auditable across sessions and agents.
- Keep the service generic while letting apps define memory semantics in SDK policy config.
- Preserve conflict-safe writes with ETag optimistic concurrency.
- Keep runtime behavior deterministic with explicit document assembly inputs.

## Core Capabilities (v2 + v1 compatibility)

- Document read/patch/replace with ETag optimistic concurrency.
- Context assembly from explicit document refs with budget controls.
- Event digest write/search.
- Retention and forget-user lifecycle operations.
- Compatibility window for v1 request shapes (`policy_id`, `binding_id`) while migrating clients.
- Pluggable provider mode:
  - `filesystem` (default local mode)
  - `azure` (Blob + optional AI Search, build-flag gated)

## SDK Policy Pattern

`mem.net` service is generic. Recommended memory categories live in SDK policy config.

- `user/profile.json`
  - key user info
  - optional `projects_index` for caller-side routing
- `user/long_term_memory.json`
  - preferences and durable user facts
- `projects/{project_id}.json`
  - project-specific memory
- event digests
  - write + search across conversations and related services

Typical agent context behavior:
- includes `profile.json` and `long_term_memory.json`
- excludes templated project docs (load them on demand via document APIs)
- event digests are retrieved via `POST /events:search` by the caller

## Architecture

```mermaid
flowchart LR
    A["Orchestrator / Agents"] --> B["mem.net API"]
    B --> C["Document Store"]
    B --> D["Event Store"]
    B --> E["Audit Store"]
    D --> F["Search (derived)"]
```

Source of truth is document/event/audit storage. Search is derived and rebuildable.

## Quick Start

### 1) Restore and build

```bash
dotnet restore MemNet.sln --configfile NuGet.Config
dotnet build MemNet.sln -c Debug
```

### 2) Run service (filesystem provider)

```bash
dotnet run --project src/MemNet.MemoryService
```

### 3) Run spec tests

```bash
dotnet tests/MemNet.MemoryService.SpecTests/bin/Debug/net8.0/MemNet.MemoryService.SpecTests.dll
```

### 4) Health check

```bash
curl -s http://localhost:5071/
```

Expected response:

```json
{
  "service": "mem.net",
  "status": "ok"
}
```

## Configuration

Runtime storage/provider behavior is configured via environment variables.
`src/MemNet.MemoryService/Policy/policy.json` is currently still used for v1 compatibility mode.

### Key environment variables

| Variable | Purpose |
|---|---|
| `MEMNET_PROVIDER` | `filesystem` or `azure` |
| `MEMNET_DATA_ROOT` | Local data root for filesystem provider |
| `MEMNET_CONFIG_ROOT` | Policy root directory (contains `policy.json`) |
| `MEMNET_AZURE_STORAGE_SERVICE_URI` | Blob service URI for azure provider |
| `MEMNET_AZURE_DOCUMENTS_CONTAINER` | Documents container |
| `MEMNET_AZURE_EVENTS_CONTAINER` | Events container |
| `MEMNET_AZURE_AUDIT_CONTAINER` | Audit container |
| `MEMNET_AZURE_SEARCH_ENDPOINT` | Optional Azure AI Search endpoint |
| `MEMNET_AZURE_SEARCH_INDEX` | Optional Azure AI Search index |
| `MEMNET_AZURE_SEARCH_SCHEMA_PATH` | Optional schema path for bootstrap tool (default: `infra/search/events-index.schema.json`) |

## Azure Provider

Azure provider code is compiled only when the build flag is enabled.

```bash
dotnet build src/MemNet.MemoryService/MemNet.MemoryService.csproj -p:MemNetEnableAzureSdk=true
```

Run with Azure provider:

```bash
MEMNET_PROVIDER=azure \
MEMNET_AZURE_STORAGE_SERVICE_URI="https://<account>.blob.core.windows.net" \
MEMNET_AZURE_DOCUMENTS_CONTAINER="memnet-documents" \
MEMNET_AZURE_EVENTS_CONTAINER="memnet-events" \
MEMNET_AZURE_AUDIT_CONTAINER="memnet-audit" \
MEMNET_AZURE_SEARCH_ENDPOINT="https://<service>.search.windows.net" \
MEMNET_AZURE_SEARCH_INDEX="<events-index>" \
dotnet run --project src/MemNet.MemoryService -p:MemNetEnableAzureSdk=true
```

If `MEMNET_PROVIDER=azure` is used without Azure SDK build flag, endpoints return `501 AZURE_PROVIDER_NOT_ENABLED`.

## Azure Bootstrap (Init)

`mem.net` runtime does not create Azure AI Search index schemas at startup.

- Blob containers are created lazily on first write.
- Search index provisioning should be done in deployment/bootstrap.

Use the bootstrap tool:

```bash
dotnet run --project tools/MemNet.Bootstrap -- azure --check
dotnet run --project tools/MemNet.Bootstrap -- azure --apply
```

`--apply` performs idempotent initialization:
- ensures Blob containers exist
- creates/updates the configured Azure AI Search index using `infra/search/events-index.schema.json`

Required permissions:
- Blob container management on the target storage account
- Search index management (`Search Service Contributor` role or admin API key)

Recommended deployment order:
1. Deploy infrastructure (Search service, Storage account, identities/roles).
2. Run bootstrap `--apply`.
3. Deploy/start `mem.net` service.

## API Quick Reference

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}` | Read document |
| `PATCH` | `/v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}` | Patch document (`If-Match` required, v2 shape without `policy_id`) |
| `PUT` | `/v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}` | Replace document (`If-Match` required, v2 shape without `policy_id`) |
| `POST` | `/v1/tenants/{tenantId}/users/{userId}/context:assemble` | Assemble explicit document refs |
| `POST` | `/v1/tenants/{tenantId}/users/{userId}/events` | Write event digest |
| `POST` | `/v1/tenants/{tenantId}/users/{userId}/events:search` | Search event digests |
| `POST` | `/v1/tenants/{tenantId}/users/{userId}/retention:apply` | Apply retention (explicit day values in v2) |
| `DELETE` | `/v1/tenants/{tenantId}/users/{userId}/memory` | Forget all user memory |

v1 compatibility note:
- `PATCH`/`PUT` still accept `policy_id` + `binding_id`.
- `context:assemble` still accepts `policy_id`.
- `retention:apply` still accepts `policy_id`.

## SDK Quickstart (.NET)

### Low-level client (`MemNet.Client`)

```csharp
using MemNet.Client;

var client = new MemNetClient(new MemNetClientOptions
{
    BaseAddress = new Uri("http://localhost:5071"),
    ServiceId = "learn-companion"
});

var scope = new MemNetScope("tenant-1", "user-1");
var profileRef = new DocumentRef("user", "profile.json");

var current = await client.GetDocumentAsync(scope, profileRef);
var updated = await client.PatchDocumentAsync(
    scope,
    profileRef,
    new PatchDocumentRequest(
        Ops:
        [
            new PatchOperation("add", "/content/projects/-", "mem.net")
        ],
        Reason: "project_update"),
    ifMatch: current.ETag);
```

### High-level agent facade (`MemNet.AgentMemory`)

```csharp
using MemNet.AgentMemory;
using MemNet.Client;

var policy = new AgentMemoryPolicy(
    "learn-companion-default",
    [
        new MemorySlotPolicy("profile", "user", "profile.json", null, LoadByDefault: true),
        new MemorySlotPolicy("long_term_memory", "user", "long_term_memory.json", null, LoadByDefault: true),
        new MemorySlotPolicy("project", "projects", null, "{project_id}.json", LoadByDefault: false)
    ]);

using var client = new MemNetClient(new MemNetClientOptions
{
    BaseAddress = new Uri("http://localhost:5071"),
    ServiceId = "learn-companion"
});

var memory = new AgentMemory(client, policy);
var scope = new MemNetScope("tenant-1", "user-1");

var prepared = await memory.PrepareTurnAsync(
    scope,
    new PrepareTurnRequest(
        RecallQuery: "recent project decisions",
        RecallTopK: 8));
```

## Testing and CI

- Local executable spec tests: `tests/MemNet.MemoryService.SpecTests`.
- GitHub Actions workflow: `.github/workflows/ci.yml`.
- CI currently runs:
  - core restore/build/spec tests
  - Azure SDK-enabled restore/build/spec tests

## Repository Map

- `MEMORY_SERVICE_SPEC.md` - technical spec aligned with current implementation.
- `SDK_SPEC.md` - SDK technical spec for `MemNet.Client` and `MemNet.AgentMemory`.
- `src/MemNet.MemoryService/Api` - HTTP entrypoint and endpoint wiring.
- `src/MemNet.MemoryService/Application` - orchestration services (`MemoryCoordinator`, lifecycle, replay).
- `src/MemNet.MemoryService/Domain` - core models, errors, and patch engine.
- `src/MemNet.MemoryService/Policy` - `PolicyRegistry` and default `policy.json`.
- `src/MemNet.MemoryService/Backends` - store abstractions and provider implementations.
- `src/MemNet.Client` - low-level .NET SDK for mem.net endpoints.
- `src/MemNet.AgentMemory` - high-level agent memory SDK facade.
- `tools/MemNet.Bootstrap` - deployment/bootstrap tool for Azure containers and AI Search index.
- `infra/search/events-index.schema.json` - source-controlled schema for event search index.
- `tests/MemNet.MemoryService.SpecTests` - executable specification tests.
- `TASK_BOARD.md` - implementation progress and backlog.
- `AGENTS.md` - development guardrails and first-principles rules.

## Roadmap (Short)

- Provider-agnostic contract tests.
- Background replay/reindex orchestration.
- Optional compaction worker with dedicated config.

## Contributing

PRs are welcome. For non-trivial changes, align proposals with `MEMORY_SERVICE_SPEC.md` and keep behavior covered by spec tests.
