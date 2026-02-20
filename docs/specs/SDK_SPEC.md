# mem.net SDK Technical Specification

Project: `mem.net` SDK  
Status: Active (pre-release)  
Target Runtime: .NET 8  
Last Updated: February 20, 2026

## 1. Purpose

Define first-party .NET SDK packages that make `mem.net` practical for agent builders while preserving a clean service boundary.

## 2. Boundary with Service

Service (`mem.net`) owns:

1. Scoped persistence.
2. ETag concurrency enforcement.
3. Context assembly endpoint.
4. Event write/search.
5. Lifecycle cleanup.

SDK owns:

1. Ergonomic API surface.
2. LLM-facing memory tool abstractions.
3. Optional application-level policy helpers.

## 3. Package Model

### 3.1 `MemNet.Client` (low-level)

Endpoint-aligned HTTP client with typed contracts and typed exceptions.

### 3.2 `MemNet.AgentMemory` (high-level)

Agent-oriented facade exposing file-like memory methods suitable for LLM tool harnesses.

## 4. First-Principles Rule (SDK)

A runtime SDK abstraction should directly support at least one of:

1. deterministic context assembly
2. write guardrails/concurrency handling
3. lifecycle invocation
4. practical LLM memory-tool use

Anything else should stay out of core runtime SDK path.

## 5. `MemNet.Client` Contract

### 5.1 Core Types

- `MemNetClient`
- `MemNetClientOptions`
- `MemNetScope` (`tenant_id`, `user_id`)
- `FileRef` (`path`)
- API contracts under `Contracts.cs`

### 5.2 Options

`MemNetClientOptions`:

- `BaseAddress` (required unless `HttpClient` already has base address)
- `HttpClient` (optional injection)
- `ServiceId` (default for `X-Service-Id` on mutations)
- `Retry` (`MaxRetries`, `BaseDelay`, `MaxDelay`)
- `JsonSerializerOptions`
- `HeaderProvider`
- request/response callbacks (`OnRequest`, `OnResponse`)

### 5.3 Endpoint-Aligned Methods

All methods accept `CancellationToken`.

- `GetServiceStatusAsync()` -> `GET /`
- `ListFilesAsync(scope, request)` -> `GET /files:list`
- `GetFileAsync(scope, file)` -> `GET /files/{**path}`
- `PatchFileAsync(scope, file, request, ifMatch, ...)` -> `PATCH /files/{**path}`
- `WriteFileAsync(scope, file, request, ifMatch, ...)` -> `PUT /files/{**path}`
- `AssembleContextAsync(scope, request)` -> `POST /context:assemble`
- `WriteEventAsync(scope, request)` -> `POST /events`
- `SearchEventsAsync(scope, request)` -> `POST /events:search`
- `ApplyRetentionAsync(scope, request)` -> `POST /retention:apply`
- `ForgetUserAsync(scope)` -> `DELETE /memory`

No low-level API requires `policy_id`, `binding_id`, or namespace selectors.

### 5.4 Retry Behavior

Default retries apply to retry-safe calls only.

Retryable conditions:

- transport failures
- `429`, `503`, `502`, `504`

Mutations and lifecycle/event writes are not blindly retried by default.

### 5.5 Error Model

Exception hierarchy:

- `MemNetException`
- `MemNetApiException` (contains status/code/request_id/details/raw body)
- `MemNetTransportException`

Expected service error envelope:

```json
{
  "error": {
    "code": "...",
    "message": "...",
    "request_id": "...",
    "details": {}
  }
}
```

### 5.6 Concurrency Helper

`MemNetClientConcurrencyExtensions.UpdateWithRetryAsync(...)` supports conflict-aware mutation retry:

1. read latest doc/etag
2. build update
3. write with `If-Match`
4. on `412`, refetch and retry (bounded)

Default max conflict retries: `3`.

## 6. `MemNet.AgentMemory` Contract

### 6.1 Primary File-Like API (official)

The official harness-facing contract is:

1. `MemoryRecallAsync(scope, query, topK)`
2. `MemoryListFilesAsync(scope, prefix, limit)`
3. `MemoryLoadFileAsync(scope, path)`
4. `MemoryPatchFileAsync(scope, path, edits, ...)`
5. `MemoryWriteFileAsync(scope, path, content, ...)`

These map naturally to tool names such as:

- `memory_recall`
- `memory_list_files`
- `memory_load_file`
- `memory_patch_file`
- `memory_write_file`

### 6.2 File Content Convention

For file-like methods, SDK writes markdown-friendly envelope payloads:

```json
{
  "content_type": "text/markdown",
  "text": "..."
}
```

This is a convention for ergonomic agent integration, not a service-mandated schema.

### 6.3 Deterministic Patch Semantics

`MemoryPatchFileAsync` uses deterministic text edits (`old_text`, `new_text`, optional `occurrence`).

Behavior:

- all edits apply atomically or request fails
- conflict retries handled via `UpdateWithRetryAsync`
- service-side semantic failures surface as typed API errors

### 6.4 Event Recall

`MemoryRecallAsync` wraps `events:search` and returns event digests for agent reasoning.

### 6.5 Optional Policy Helper Layer

`MemNet.AgentMemory` also includes slot/policy helper APIs (`PrepareTurnAsync`, slot load/patch/replace).

These are optional app-facing helpers and are not the primary LLM-facing integration path.

Pure file-tool usage is supported by passing an empty policy:

```csharp
var memory = new AgentMemory(client, new AgentMemoryPolicy("default", []));
```

## 7. Testing Strategy

Minimum SDK validation expectations:

1. Unit tests for retry/error mapping.
2. Integration tests against in-process mem.net API host.
3. Deterministic patch and ETag conflict coverage.
4. File-like tool flow coverage (`load/write/patch/list/recall`).

## 8. Versioning and Compatibility

- SDK packages use SemVer.
- Current phase is pre-release; breaking changes are allowed before stable v1.
- SDK contract targets mem.net v2 file-first API boundary.

## 9. Design Guidance for Agent Builders

Recommended pattern:

1. Prime core files once per session (`context:assemble` + selected files).
2. Use file-like tools during dialogue.
3. Refresh in-memory snapshot after successful write/patch.
4. Use `memory_list_files("projects/")` for restart-safe project discovery.
5. Use `memory_recall(query, topK)` for event-based recall instead of loading large histories into file context.
