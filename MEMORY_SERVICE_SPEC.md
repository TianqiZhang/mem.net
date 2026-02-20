# mem.net Memory Service Technical Specification

Project: `mem.net`  
Status: Active (pre-release)  
Version: v2 file-first boundary  
Last Updated: February 20, 2026

## 1. Purpose

`mem.net` is a scoped memory infrastructure service for multi-agent systems.

Scope key: `(tenant_id, user_id)`

The service provides:

1. Durable file storage.
2. Conflict-safe mutation with ETag optimistic concurrency.
3. Deterministic context assembly from explicit file refs.
4. Event digest write/search for recall.
5. Lifecycle cleanup (retention and forget-user).

## 2. Runtime Boundary

`mem.net` runtime is infrastructure-only.

In scope:

1. Slot-agnostic file APIs.
2. Deterministic mutation/assembly behavior.
3. Event indexing/search plumbing.
4. Cleanup execution.

Out of scope:

1. App-specific memory categories (`profile`, `projects`, etc.).
2. Server-side policy registries, slot bindings, or schema registries.
3. Prompt strategy and context engineering owned by caller/SDK.

## 3. Architecture

- API host: ASP.NET Core Minimal API.
- Core orchestration: `MemoryCoordinator`, `DataLifecycleService`.
- Persistence backend (single provider mode at runtime):
  - `filesystem` (default)
  - `azure` (Blob + optional AI Search, build-flag gated)

Source of truth is files/events/audits persisted in storage. Search index is derived and rebuildable.

## 4. Storage Layout (reference)

Reference physical layout:

- `/tenants/{tenant_id}/users/{user_id}/files/{path}`
- `/tenants/{tenant_id}/users/{user_id}/events/{event_id}.json`
- `/tenants/{tenant_id}/users/{user_id}/audit/{change_id}.json`
- `/tenants/{tenant_id}/users/{user_id}/snapshots/...` (optional external snapshot material; lifecycle cleanup only)

## 5. Core Data Contracts

### 5.1 Document Envelope

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

- `content` is a JSON object.
- Service stores envelope fields transparently.
- Service does not enforce app-specific schema semantics.

### 5.2 Event Digest

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
  "evidence": {
    "source": "chat",
    "conversation_id": "c_123",
    "message_ids": ["m1"]
  }
}
```

Notes:

- `evidence` is opaque JSON (`JsonNode`) and is persisted without schema interpretation.

## 6. API Surface

All endpoints are scoped by route:

`/v1/tenants/{tenantId}/users/{userId}/...`

### 6.1 Service Status

- `GET /`

### 6.2 List Files

- `GET /files:list?prefix={optional}&limit={optional}`

Behavior:

- `limit` default: `100`
- `limit` allowed range: `1..500`
- `prefix` is optional
- `prefix` must not contain `..`

Response:

```json
{
  "files": [
    {
      "path": "projects/mem.net.md",
      "last_modified_utc": "2026-02-19T12:34:56Z"
    }
  ]
}
```

### 6.3 Get File

- `GET /files/{**path}`

Response:

```json
{
  "etag": "\"...\"",
  "document": { "...": "..." }
}
```

### 6.4 Patch File

- `PATCH /files/{**path}`

Headers:

- `If-Match` required
- `X-Service-Id` optional (defaults to `unknown-service`)

Request body supports two mutation styles:

1. JSON patch operations (`ops[]`)
2. Deterministic text edits (`edits[]`)

At least one of `ops[]` or `edits[]` must be non-empty.

Deterministic text edit shape:

```json
{
  "edits": [
    {
      "old_text": "## Preferences\n- concise answers\n",
      "new_text": "## Preferences\n- concise answers\n- include tradeoffs first\n",
      "occurrence": 1
    }
  ],
  "reason": "preference_update",
  "evidence": {
    "source": "chat",
    "conversation_id": "c_123",
    "trace_id": "abc-123"
  }
}
```

Text edit rules:

- target is `document.content.text` string
- `old_text` must be non-empty
- no match => `PATCH_MATCH_NOT_FOUND`
- multiple matches without `occurrence` => `PATCH_MATCH_AMBIGUOUS`
- invalid `occurrence` => `PATCH_OCCURRENCE_OUT_OF_RANGE`

### 6.5 Write File

- `PUT /files/{**path}`

Headers:

- `If-Match` required (`*` allowed for create-if-missing)
- `X-Service-Id` optional

Request body:

```json
{
  "document": { "...": "..." },
  "reason": "manual_rewrite",
  "evidence": {
    "source": "tool",
    "tool_call_id": "call_01"
  }
}
```

### 6.6 Assemble Context

- `POST /context:assemble`

Request:

```json
{
  "files": [
    { "path": "profile.md" },
    { "path": "long_term_memory.md" }
  ],
  "max_docs": 8,
  "max_chars_total": 40000
}
```

Behavior:

- explicit file refs only
- request order preserved
- defaults: `max_docs=4`, `max_chars_total=30000`
- missing files are skipped (not an error)
- over-budget files appear in `dropped_files[]`
- event recall is separate (`events:search`)

### 6.7 Write Event Digest

- `POST /events`

Request:

```json
{
  "event": { "...": "..." }
}
```

Behavior:

- route scope must match `event.tenant_id` and `event.user_id`
- `event_id` and `digest` are required
- returns `202 Accepted` on persistence

### 6.8 Search Events

- `POST /events:search`

Supports filters:

- `query`
- `service_id`
- `source_type`
- `project_id`
- `from`/`to`
- `top_k`

### 6.9 Apply Retention

- `POST /retention:apply`

Request:

```json
{
  "events_days": 365,
  "audit_days": 365,
  "snapshots_days": 60,
  "as_of_utc": null
}
```

Rules:

- day values must be `>= 0`

### 6.10 Forget User

- `DELETE /memory`

Deletes all user-scoped files/events/audits/snapshots and derived search docs (when applicable).

## 7. Validation and Limits

File mutation enforcement:

- `If-Match` required for `PATCH` and `PUT`
- max patch op/edit count: `100`
- envelope field requirements: `doc_id`, `schema_id`, `schema_version`
- envelope max serialized size: `256_000` chars (`DOCUMENT_SIZE_EXCEEDED`)

Path validation:

- file path must not be empty
- file path and prefix must not contain `..`

## 8. Concurrency

Concurrency model is optimistic ETag.

Mutation flow:

1. Caller reads current ETag.
2. Caller sends mutation with `If-Match`.
3. Service returns:
   - success with new ETag
   - `412 ETAG_MISMATCH` with latest ETag details on conflict

No multi-document transaction semantics are provided.

## 9. Error Model

Canonical status codes:

- `400` invalid request
- `404` not found
- `412` ETag mismatch
- `422` semantic validation failure
- `500/503` unhandled/dependency errors

Error envelope:

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

## 10. Observability and Audit

Minimum telemetry expectations:

- endpoint latency
- mutation success/failure counts (including `412`, `422`)
- event search latency
- retention/forget deletion counts

Every mutation writes an audit record with:

- actor
- tenant/user scope
- target path
- ETag transition
- reason
- operation payload metadata
- opaque `evidence`

## 11. Security Boundary

`mem.net` does not store or expose system-level policy/prompt governance.

`mem.net` is responsible for:

- scoped storage isolation
- concurrency and integrity guarantees
- auditable mutation trails

Application semantic safety remains caller/SDK responsibility.

## 12. Deployment Notes

Provider selection:

- `MEMNET_PROVIDER=filesystem` (default local mode)
- `MEMNET_PROVIDER=azure` (Blob + optional AI Search)

Azure build flag:

- Azure provider requires `-p:MemNetEnableAzureSdk=true`
- Without build flag, Azure endpoints return `501 AZURE_PROVIDER_NOT_ENABLED`

Initialization model:

- Blob containers are created lazily on first writes
- Azure AI Search index provisioning is deployment/bootstrap responsibility
- bootstrap tool: `tools/MemNet.Bootstrap` (`--check`, `--apply`)

## 13. Pre-Release Contract Policy

`mem.net` is pre-release.

- Breaking changes are allowed before first stable release.
- Service remains policy-free and namespace-free at runtime boundary.
- Canonical file contract is `/files/{**path}` + path-only `context:assemble` refs.
