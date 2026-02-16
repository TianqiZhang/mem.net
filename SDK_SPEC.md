# mem.net SDK Technical Specification

Project: `mem.net` SDK  
Status: Active pre-release implementation; file-first SDK primitives available  
Last Updated: February 16, 2026

## 1. Purpose
Define a first-party .NET SDK that:
- makes `mem.net` easier to consume from applications
- provides a stable memory abstraction for agent builders
- owns application-level memory semantics outside `mem.net` runtime

## 2. Boundary with mem.net Service
Service (`mem.net`) owns:
- scope isolation
- durable document/event storage
- ETag concurrency
- event search/indexing
- lifecycle cleanup

SDK owns:
- LLM-facing memory tool contract
- optional policy/slot config for application semantics
- deterministic agent-facing context composition strategy

## 3. First-Principles Fit
Every SDK abstraction must map to one of:
- slot resolution
- write guardrails
- deterministic context assembly
- lifecycle invocation

If it does not support one of these capabilities, keep it out of runtime SDK model.

## 4. Package Layout
Two NuGet packages:

1. `MemNet.Client`
- low-level HTTP API wrapper
- endpoint-aligned contracts for v2 service API
- typed error mapping, retries, diagnostics

2. `MemNet.AgentMemory`
- high-level agent-oriented API
- file-like memory tools for LLM harnesses
- optional slot/policy layer for app-owned memory conventions

Optional future package:
- `MemNet.Testing`

## 5. Target Frameworks
- `net8.0` minimum for first release.

## 6. Low-Level API (`MemNet.Client`)
### 6.1 Core Types
- `MemNetClient`
- `MemNetClientOptions`
- `MemNetScope` (`tenantId`, `userId`)
- `FileRef` (`path`)
- `ApiErrorEnvelope` / `ApiError`

### 6.2 Client Options
`MemNetClientOptions`:
- `BaseAddress` (required)
- `ServiceId` (default `X-Service-Id` for document mutations)
- `HttpClient` or `HttpMessageHandler` injection
- `Retry` options (max retries, backoff)
- `JsonSerializerOptions` override
- `HeaderProvider` callback for auth/gateway headers
- diagnostics callbacks (`OnRequest`, `OnResponse`)

### 6.3 Methods (Endpoint-Aligned)
All methods accept `CancellationToken`.

- `GetServiceStatusAsync()` -> `GET /`
- `GetFileAsync(MemNetScope scope, FileRef file)` -> `GET /files/{**path}`
- `PatchFileAsync(MemNetScope scope, FileRef file, PatchDocumentRequest request, string ifMatch)` -> `PATCH /files/{**path}`
- `WriteFileAsync(MemNetScope scope, FileRef file, ReplaceDocumentRequest request, string ifMatch)` -> `PUT /files/{**path}`
- `AssembleContextAsync(MemNetScope scope, AssembleContextRequest request)` -> `POST /context:assemble`
- `WriteEventAsync(MemNetScope scope, WriteEventRequest request)` -> `POST /events` (`202 Accepted`)
- `SearchEventsAsync(MemNetScope scope, SearchEventsRequest request)` -> `POST /events:search`
- `ApplyRetentionAsync(MemNetScope scope, ApplyRetentionRequest request)` -> `POST /retention:apply`
- `ForgetUserAsync(MemNetScope scope)` -> `DELETE /memory`

No low-level method requires `policy_id` or `binding_id`.

Phase 17 status:
- service and SDK now use path-only `/files/{**path}` semantics
- namespace-based SDK references are removed from public surface
- canonical file patch payload uses deterministic text `edits[]` (`old_text`, `new_text`, optional `occurrence`)

### 6.4 Response Shapes
- file operations: `ETag` + `DocumentEnvelope`
- lifecycle operations: `ForgetUserResult`, `RetentionSweepResult`

## 7. Error Model
### 7.1 Exceptions
- `MemNetException` (base)
- `MemNetApiException`
  - `StatusCode`
  - `Code`
  - `RequestId`
  - `Details`
- `MemNetTransportException`

### 7.2 Mapping
- Non-success HTTP -> parse service envelope -> throw `MemNetApiException`.
- Parse failures -> `MemNetException` with safe raw snippet.

Expected envelope:
```json
{ "error": { "code": "...", "message": "...", "request_id": "...", "details": {} } }
```

## 8. Concurrency Helper
Low-level mutations require explicit `ifMatch`.

High-level helper:
- `UpdateWithRetryAsync(...)`

Behavior:
1. fetch current doc + etag
2. caller callback builds mutation
3. write with `If-Match`
4. on `412 ETAG_MISMATCH`, refetch and bounded retry

Default max conflict retries: `3`.

## 9. High-Level API (`MemNet.AgentMemory`)
### 9.1 Official LLM Tool Contract
The official harness-facing contract is file-like and intentionally minimal:

1. `memory_recall(query, top_k)`
2. `memory_load_file(path)`
3. `memory_patch_file(path, edits)`
4. `memory_write_file(path, content)`

Recommended defaults:
- LLM-facing files use markdown (`.md`) where possible.
- machine/index records remain JSON where required (event contract).

### 9.2 File Tool Semantics
`memory_load_file(path)`:
- returns file content plus metadata (`path`, `content_type`, `etag`).

`memory_write_file(path, content)`:
- full file replacement
- SDK/harness manages `If-Match` and retries internally.

`memory_patch_file(path, edits)`:
- deterministic text edits (`old_text`, `new_text`, optional `occurrence`)
- all edits apply or none apply
- SDK maps failures to explicit actionable errors (`not_found`, `ambiguous_match`, `etag_conflict`).

`memory_recall(query, top_k)`:
- wraps event search with deterministic result shaping.

### 9.3 Optional Policy Layer
Slot/policy APIs may remain as app-facing helpers, but are not the primary LLM-facing contract.

## 10. Validation Strategy (SDK-Owned)
Primary validation target is deterministic file editing:
- patch edit structural validation
- deterministic `old_text` matching with optional occurrence
- bounded edit count and payload limits

Optional slot/policy validation remains available only for policy-layer APIs.

## 11. Retries and Resilience
Default retry for idempotent calls:
- transport failures, `429`, `503`
- exponential backoff with jitter

Mutations:
- no blind retries
- only conflict-aware retry helper for `412`

`POST /events` is not retried by default to avoid accidental duplicates.

## 12. Observability
- surface `request_id` and status/error codes
- expose request lifecycle hooks
- optional OTel integration in later phase

## 13. Versioning
- SemVer for SDK packages
- SDK v1 targets service v2 contract
- pre-release period allows breaking changes before first stable release

## 14. Rollout Plan
### Phase A
- finalize specs and package skeleton

### Phase B
- implement `MemNet.Client` against v2 contract
- add typed errors + retry baseline

### Phase C
- implement `MemNet.AgentMemory` policy loader + slot operations
- implement `PrepareTurnAsync`
- add conflict helper tests

### Phase D
- docs/samples for practical agent use case

## 14.1 Phase 17 Outcome
1. API-first refactor completed: namespace removed from public file APIs.
2. SDK refactor completed: path-only file primitives in `MemNet.Client`.
3. `MemNet.AgentMemory` exposes file-like tool methods for LLM harness workflows.

## 15. Acceptance Criteria (SDK v1)
1. Low-level client covers all v2 endpoints.
2. No low-level API requires policy/binding concepts.
3. High-level API exposes official 4-tool file-like contract for agent harnesses.
4. Typed exceptions include service `code` and `request_id`.
5. Concurrency helper is deterministic and bounded.
6. Tests run offline against local harness.

## 16. Implementation Status
Current implementation includes:
- `src/MemNet.Client` path-based file endpoint client, typed errors, retry policy, and `UpdateWithRetryAsync`.
- `src/MemNet.AgentMemory` high-level file-like tool methods (`MemoryRecallAsync`, `MemoryLoadFileAsync`, `MemoryPatchFileAsync`, `MemoryWriteFileAsync`) plus optional slot/policy helpers.
- executable SDK contract coverage in `tests/MemNet.MemoryService.SpecTests`.
