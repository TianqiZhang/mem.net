# mem.net SDK Technical Specification

Project: `mem.net` SDK  
Status: Active v1 implementation  
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
- policy config loading
- slot/binding resolution (`slot -> namespace/path`)
- schema/path guardrails for app documents
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
- policy-driven slot model and validation
- context preparation (`load docs + search events`)

Optional future package:
- `MemNet.Testing`

## 5. Target Frameworks
- `net8.0` minimum for first release.

## 6. Low-Level API (`MemNet.Client`)
### 6.1 Core Types
- `MemNetClient`
- `MemNetClientOptions`
- `MemNetScope` (`tenantId`, `userId`)
- `DocumentRef` (`namespace`, `path`)
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
- `GetDocumentAsync(MemNetScope scope, DocumentRef doc)` -> `GET /documents/{namespace}/{path}`
- `PatchDocumentAsync(MemNetScope scope, DocumentRef doc, PatchDocumentRequest request, string ifMatch)` -> `PATCH /documents/{namespace}/{path}`
- `ReplaceDocumentAsync(MemNetScope scope, DocumentRef doc, ReplaceDocumentRequest request, string ifMatch)` -> `PUT /documents/{namespace}/{path}`
- `AssembleContextAsync(MemNetScope scope, AssembleContextRequest request)` -> `POST /context:assemble`
- `WriteEventAsync(MemNetScope scope, WriteEventRequest request)` -> `POST /events` (`202 Accepted`)
- `SearchEventsAsync(MemNetScope scope, SearchEventsRequest request)` -> `POST /events:search`
- `ApplyRetentionAsync(MemNetScope scope, ApplyRetentionRequest request)` -> `POST /retention:apply`
- `ForgetUserAsync(MemNetScope scope)` -> `DELETE /memory`

No low-level method requires `policy_id` or `binding_id`.

### 6.4 Response Shapes
- document operations: `ETag` + `DocumentEnvelope`
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
### 9.1 Policy Model (SDK-Owned)
Example config file (`agent-memory-policy.json`):
```json
{
  "policy_id": "learn-companion-default",
  "slots": [
    {
      "slot_id": "profile",
      "namespace": "user",
      "path": "profile.json",
      "load_by_default": true,
      "patch_rules": {
        "allowed_paths": ["/content/profile", "/content/projects"],
        "required_content_paths": ["/profile"]
      }
    },
    {
      "slot_id": "long_term_memory",
      "namespace": "user",
      "path": "long_term_memory.json",
      "load_by_default": true
    },
    {
      "slot_id": "project",
      "namespace": "projects",
      "path_template": "{project_id}.json",
      "load_by_default": false
    }
  ]
}
```

### 9.2 Primary Interface
`AgentMemory` exposes:
- `PrepareTurnAsync(...)`
- `LoadSlotAsync(...)`
- `PatchSlotAsync(...)`
- `ReplaceSlotAsync(...)`
- `RememberAsync(...)` (event write)
- `RecallAsync(...)` (event search)
- `ForgetUserAsync(...)`

### 9.3 PrepareTurn Semantics
`PrepareTurnAsync` does:
1. resolve default-load slots from SDK policy
2. load those docs (via `context:assemble` or batched `GET`)
3. execute `events:search` using caller-provided query/filter
4. return deterministic `PreparedMemory`

No server-side policy assumptions are required.

## 10. Validation Strategy (SDK-Owned)
By default, high-level SDK validates before mutation:
- slot exists and resolves to concrete doc path
- patch path allowlist
- required content paths
- optional content/array limits

Validation can be configured strict/lenient, but default is strict for safety.

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

## 13. Versioning and Compatibility
- SemVer for SDK packages
- SDK v1 targets service v2 contract
- optional temporary compatibility adapter for service v1 may exist during migration

## 14. Service/SDK Compatibility Matrix
| Service Shape | MemNet.Client | MemNet.AgentMemory |
|---|---|---|
| v1 (policy/binding in API) | optional compatibility adapter | supported through adapter only |
| v2 (policy-free API) | native | native |

Target steady state: v2 native only.

## 15. Rollout Plan
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
- migration notes from v1 contract

## 16. Acceptance Criteria (SDK v1)
1. Low-level client covers all v2 endpoints.
2. No low-level API requires policy/binding concepts.
3. High-level API supports policy-driven slot model for agents.
4. Typed exceptions include service `code` and `request_id`.
5. Concurrency helper is deterministic and bounded.
6. Tests run offline against local harness.

## 17. Implementation Status
Current implementation includes:
- `src/MemNet.Client` low-level endpoint client, typed errors, retry policy, and `UpdateWithRetryAsync`.
- `src/MemNet.AgentMemory` high-level policy/slot facade for agent integration.
- executable SDK contract coverage in `tests/MemNet.MemoryService.SpecTests`.
