# mem.net Memory Service Technical Specification

Project: `mem.net`  
Status: Active v2 baseline, file-first contract planned for Phase 17  
Version: 2.0 (target)  
Last Updated: February 16, 2026

## 1. Purpose
`mem.net` is a shared memory service for multi-agent systems using `(tenant_id, user_id)` scope.

The v2 service focuses on infrastructure primitives:
- durable document storage
- optimistic concurrency via ETag
- event digest write + search
- lifecycle cleanup (retention and forget-user)

`mem.net` does not own application-level memory semantics (policy categories, slot names, schema preferences).

## 2. First-Principles Scope (v2)
Only these runtime capabilities are in scope:
1. Scope isolation and document/event storage.
2. Conflict-safe writes with explicit `If-Match` semantics.
3. Deterministic context assembly from caller-provided document refs.
4. Lifecycle cleanup execution.

Anything outside these capabilities belongs in SDK/application layers.

## 3. Out of Scope (v2 Runtime)
- Server-side policy registry and binding model.
- Server-side schema/path guardrails for app-specific documents.
- Server-side memory category semantics (profile, projects, long-term memory, etc.).
- Prompt/context strategy owned by agent code.

## 4. High-Level Architecture
- API service (ASP.NET Core Minimal API)
  - document read/patch/replace
  - explicit-doc context assembly
  - event write/search
  - retention/forget-user
- Persistence provider
  - `filesystem` (default)
  - `azure` (Blob + optional AI Search, build-flag gated)
- Derived event index
  - local in-memory scoring for filesystem provider
  - Azure AI Search when configured for azure provider

Source of truth is documents/events/audits/snapshots in storage. Search index is derived state.

## 5. Storage Layout (Reference)
`/tenants/{tenant_id}/users/{user_id}/documents/{namespace}/{path}`  
`/tenants/{tenant_id}/users/{user_id}/events/{event_id}.json`  
`/tenants/{tenant_id}/users/{user_id}/audit/{change_id}.json`  
`/tenants/{tenant_id}/users/{user_id}/snapshots/{conversation_id}/{snapshot_id}.json`

## 6. Core Data Contracts
### 6.1 Document Envelope
```json
{
  "doc_id": "uuid",
  "schema_id": "app.schema.id",
  "schema_version": "1.0.0",
  "created_at": "2026-02-15T00:00:00Z",
  "updated_at": "2026-02-15T00:00:00Z",
  "updated_by": "service-id",
  "content": {}
}
```

Notes:
- `schema_id` and `schema_version` are stored transparently.
- Service does not interpret app schema semantics beyond payload sanity/limits.

### 6.2 Event Digest
```json
{
  "event_id": "evt_01",
  "tenant_id": "t1",
  "user_id": "u1",
  "service_id": "assistant-a",
  "timestamp": "2026-02-15T00:00:00Z",
  "source_type": "chat",
  "digest": "short summary",
  "keywords": ["memory"],
  "project_ids": ["project-alpha"],
  "snapshot_uri": "blob://...",
  "evidence": {
    "message_ids": ["m1"],
    "start": 1,
    "end": 2
  }
}
```

## 7. API Specification (v2)
All endpoints are server-to-server and scoped by `(tenantId, userId)`.

### 7.1 Service Status
`GET /`

### 7.2 Get Document
`GET /v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}`

Response:
```json
{
  "etag": "\"...\"",
  "document": { "...": "..." }
}
```

### 7.3 Patch Document
`PATCH /v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}`

Headers:
- `If-Match` required
- `X-Service-Id` optional (defaults to `unknown-service`)

Request body:
```json
{
  "ops": [
    { "op": "replace", "path": "/content/preferences/0", "value": "Use concise answers." }
  ],
  "reason": "live_update",
  "evidence": {
    "conversation_id": "c_123",
    "message_ids": ["m1"],
    "snapshot_uri": null
  }
}
```

### 7.4 Replace Document
`PUT /v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}`

Headers:
- `If-Match` required
- `X-Service-Id` optional

Request body:
```json
{
  "document": { "...": "..." },
  "reason": "manual_rewrite",
  "evidence": {
    "conversation_id": "c_123",
    "message_ids": ["m2"],
    "snapshot_uri": null
  }
}
```

### 7.5 Assemble Context (Explicit Refs)
`POST /v1/tenants/{tenantId}/users/{userId}/context:assemble`

Request body:
```json
{
  "documents": [
    { "namespace": "user", "path": "profile.json" },
    { "namespace": "user", "path": "long_term_memory.json" }
  ],
  "max_docs": 8,
  "max_chars_total": 40000
}
```

Response includes:
- `documents[]` with resolved doc content + etag
- `dropped_documents[]` when budgets prevent inclusion
- missing docs are omitted (not an error)

### 7.6 Write Event Digest
`POST /v1/tenants/{tenantId}/users/{userId}/events`

Request body:
```json
{
  "event": { "...": "..." }
}
```

Returns `202 Accepted` when persisted.

### 7.7 Search Events
`POST /v1/tenants/{tenantId}/users/{userId}/events:search`

Supports filtering by query/service/source/project/time range/top-k.

### 7.8 Apply Retention
`POST /v1/tenants/{tenantId}/users/{userId}/retention:apply`

Request body:
```json
{
  "events_days": 365,
  "audit_days": 365,
  "snapshots_days": 60,
  "as_of_utc": null
}
```

### 7.9 Forget User
`DELETE /v1/tenants/{tenantId}/users/{userId}/memory`

### 7.10 Native File API (Phase 17 Target)
To align with agent file-like memory tools, the canonical native contract is a file API family:

- `GET /v1/tenants/{tenantId}/users/{userId}/files/{**path}`
- `PUT /v1/tenants/{tenantId}/users/{userId}/files/{**path}`
- `PATCH /v1/tenants/{tenantId}/users/{userId}/files/{**path}`

Route decision:
- Native file-first semantics use `/files`.
- Existing `/documents/{namespace}/{path}` remains current baseline API until `/files` is implemented.

File read response shape:
```json
{
  "etag": "\"...\"",
  "file": {
    "path": "/long_term_memory.md",
    "content_type": "text/markdown",
    "content": "## Preferences\n- concise answers\n"
  }
}
```

File write request shape:
```json
{
  "content_type": "text/markdown",
  "content": "## Preferences\n- concise answers\n",
  "reason": "manual_update"
}
```

File patch request shape (deterministic text patch):
```json
{
  "edits": [
    {
      "old_text": "## Preferences\n- concise answers\n",
      "new_text": "## Preferences\n- concise answers\n- include tradeoffs first\n",
      "occurrence": 1
    }
  ],
  "reason": "preference_update"
}
```

Patch semantics:
- all-or-nothing
- exact match + optional occurrence selector
- ambiguous or missing match returns `422`
- `If-Match` required for write/patch

## 8. Validation Rules (Runtime)
For document mutations, service enforces:
- `If-Match` optimistic concurrency.
- max patch operation count (`100`).
- request/body structural validity.
- envelope payload sanity and bounded size limits (service-level guardrails).

For native file patch (Phase 17 target), service will enforce:
- deterministic text-edit matching (`old_text`, `new_text`, `occurrence`)
- bounded edit count and bounded file payload size
- explicit rejection for ambiguous/unmatched edits

For events, service enforces required API contract fields.

Service does not enforce app-specific path/schema policies.

## 9. Conflict Strategy
ETag optimistic concurrency:
1. Reject stale write (`412 ETAG_MISMATCH`).
2. Caller refetches latest document/etag.
3. Caller rebases and retries.

Service does not provide multi-document transactions.

## 10. Context Assembly Behavior
- Caller provides explicit document refs.
- Service reads refs in request order.
- `max_docs` and `max_chars_total` budgets are enforced deterministically.
- Returned docs include etag + document envelope.
- `events:search` remains a separate API call.

## 11. Retention and Deletion
`retention:apply` removes expired:
- event records
- audit records
- snapshots
- derived search docs when enabled

`DELETE /memory` removes all user-scoped:
- documents
- events
- audits
- snapshots
- derived search docs when enabled

## 12. Error Model
Canonical status codes:
- `400` invalid request
- `404` missing resource
- `412` ETag mismatch
- `422` semantic validation error
- `500/503` service/dependency failures

Error payload:
```json
{
  "error": {
    "code": "ETAG_MISMATCH",
    "message": "If-Match does not match latest document version.",
    "request_id": "...",
    "details": {
      "latest_etag": "\"...\""
    }
  }
}
```

## 13. Security Boundary
System-wide safety/compliance policy is outside memory storage and not writable through memory APIs.

`mem.net` enforces transport/storage integrity, optimistic concurrency, and auditability.
Agent/application semantic safety is handled by SDK/application policy logic.

## 14. Observability Requirements
Minimum telemetry:
- request latency by endpoint
- mutation success/error counts (`412`, `422`, etc.)
- event search latency
- retention/forget-user deletion counts

Audit records include actor, tenant/user, target path, ETag transition, reason, and evidence pointers.

## 15. Deployment Notes
- Provider selected via `MemNet:Provider` or `MEMNET_PROVIDER`.
- `filesystem` provider runs locally with no cloud dependencies.
- `azure` provider requires Azure SDK build flag and Azure configuration.
- If azure provider is selected without Azure SDK build flag, API returns `501 AZURE_PROVIDER_NOT_ENABLED`.
- Azure AI Search index provisioning is deployment/bootstrap responsibility (not runtime startup mutation).
- Bootstrap tool: `tools/MemNet.Bootstrap` with `--check` and `--apply`.
- Event index schema artifact: `infra/search/events-index.schema.json`.

## 16. Pre-Release Change Policy
`mem.net` is pre-release and has no external compatibility commitments yet.

- breaking API and contract changes are allowed before first public stable release
- service runtime remains policy-free (no `policy_id`/`binding_id` selectors)
- policy/slot/schema guardrails belong to SDK/application layers
- `/files` rollout can replace `/documents` semantics without legacy-shape support requirements

## 17. Deferred Extensions
- dedicated compaction worker and compaction-specific config
- replay/reindex background orchestration
- multi-document transaction semantics

## 18. Acceptance Criteria (v2)
1. Service API supports policy-free document mutation flows.
2. ETag conflict semantics remain unchanged and explicit.
3. Context assembly is explicit-doc based and deterministic.
4. Event write/search and lifecycle endpoints remain functional.
5. SDK/application can own memory semantics without server coupling.

## 19. Implementation Status
Current implementation satisfies v2 acceptance criteria with a single policy-free request shape.
