# mem.net Memory Service Technical Specification

Project: `mem.net`  
Status: Active v1 implementation  
Version: 1.2  
Last Updated: February 16, 2026

## 1. Purpose
`mem.net` is a shared memory service for multi-agent systems using a unified `(tenant_id, user_id)` identity.

The service provides:
- durable document storage with optimistic concurrency
- policy-based write guardrails
- deterministic context assembly
- event digest write/search
- lifecycle operations (retention and forget-user)

## 2. First-Principles Scope (v1)
Only four runtime capabilities are in scope:
1. Resolve logical memory bindings to concrete files.
2. Enforce safe writes (path allowlists + size/shape limits).
3. Assemble context deterministically with bounded budgets.
4. Apply lifecycle cleanup rules.

If a new concept does not directly support one of these four capabilities, it is out of v1 runtime scope.

## 3. Out of Scope (v1)
- Runtime confidence gates.
- Runtime compaction jobs.
- Separate runtime schema registry.
- End-user editing UI.
- Autonomous policy generation by LLMs.

These may be added later as optional modules.

## 4. High-Level Architecture
- API service (ASP.NET Core Minimal API)
  - document read/patch/replace
  - context assembly
  - event write/search
  - retention/forget-user
- Persistence provider
  - `filesystem` (default)
  - `azure` (Blob + optional AI Search, build-flag gated)
- Derived index
  - local in-memory scoring for filesystem provider
  - Azure AI Search when configured for azure provider

Source of truth is document/event/audit blobs/files. Search index is derived.

## 5. Storage Layout (Reference)
`/tenants/{tenant_id}/users/{user_id}/documents/{namespace}/{path}`  
`/tenants/{tenant_id}/users/{user_id}/events/{event_id}.json`  
`/tenants/{tenant_id}/users/{user_id}/audit/{change_id}.json`  
`/tenants/{tenant_id}/users/{user_id}/snapshots/{conversation_id}/{snapshot_id}.json`

## 6. Policy Model
Runtime behavior is configured from `Policy/policy.json` (or from `MEMNET_CONFIG_ROOT` pointing to the policy directory).

### 6.1 Policy File Shape
```json
{
  "policies": [
    {
      "policy_id": "project-copilot-v1",
      "document_bindings": [
        {
          "binding_id": "user_dynamic",
          "namespace": "user",
          "path": "user_dynamic.json",
          "path_template": null,
          "schema_id": "memory.user.dynamic",
          "schema_version": "1.0.0",
          "max_chars": 18000,
          "read_priority": 20,
          "write_mode": "restricted_patch",
          "allowed_paths": ["/preferences", "/durable_facts", "/pending_confirmations", "/projects_index"],
          "required_content_paths": ["/preferences"],
          "max_content_chars": 14000,
          "max_array_items": 300
        }
      ],
      "retention_rules": {
        "snapshots_days": 60,
        "events_days": 365,
        "audit_days": 365
      }
    }
  ]
}
```

### 6.2 Binding Semantics
- `path` for fixed documents.
- `path_template` for project-scoped documents (for example `{project_id}.json`).
- `allowed_paths` controls writable `PATCH` paths.
- `required_content_paths`, `max_content_chars`, and `max_array_items` enforce structure/size constraints.
- `write_mode` controls replace permissions (`replace_allowed` required for `PUT`).

## 7. Core Data Contracts
### 7.1 Document Envelope
```json
{
  "doc_id": "uuid",
  "schema_id": "memory.user.dynamic",
  "schema_version": "1.0.0",
  "created_at": "2026-02-15T00:00:00Z",
  "updated_at": "2026-02-15T00:00:00Z",
  "updated_by": "service-id",
  "content": {}
}
```

### 7.2 Event Digest
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

### 7.3 Replay Patch Record (Internal)
```json
{
  "replay_id": "rpl_01",
  "target_binding_id": "user_dynamic",
  "target_path": "user_dynamic.json",
  "base_etag": "\"0x...\"",
  "ops": [
    { "op": "add", "path": "/content/preferences/-", "value": "Prefer concise responses." }
  ],
  "snapshot_uri": "blob://...",
  "message_ids": ["m1"]
}
```

## 8. API Specification
All endpoints are server-to-server.

### 8.1 Get Document
`GET /v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}`

### 8.2 Patch Document
`PATCH /v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}`

Headers:
- `If-Match` required.

Request body:
```json
{
  "policy_id": "project-copilot-v1",
  "binding_id": "user_dynamic",
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

### 8.3 Replace Document (Restricted)
`PUT /v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{path}`

Requires `If-Match`. Allowed only when binding `write_mode == replace_allowed`.

### 8.4 Assemble Context
`POST /v1/tenants/{tenantId}/users/{userId}/context:assemble`

Request body:
```json
{
  "policy_id": "project-copilot-v1",
  "max_docs": 4,
  "max_chars_total": 30000
}
```

Response includes:
- `documents[]`
- `dropped_bindings[]`

### 8.5 Write Event Digest
`POST /v1/tenants/{tenantId}/users/{userId}/events`

### 8.6 Search Events
`POST /v1/tenants/{tenantId}/users/{userId}/events:search`

Supports filtering by query/service/source/project/time range/top-k.

### 8.7 Apply Retention
`POST /v1/tenants/{tenantId}/users/{userId}/retention:apply`

Request body:
```json
{
  "policy_id": "project-copilot-v1",
  "as_of_utc": null
}
```

### 8.8 Forget User
`DELETE /v1/tenants/{tenantId}/users/{userId}/memory`

## 9. Validation Rules (Runtime)
For patch/replace operations, the service enforces:
- `If-Match` optimistic concurrency.
- binding existence + namespace/path consistency.
- `allowed_paths` allowlist checks for patch ops.
- envelope `schema_id` and `schema_version` must match binding values.
- total document size <= binding `max_chars`.
- content size <= binding `max_content_chars` (if set).
- required content paths present.
- recursive array length <= binding `max_array_items` (if set).
- max patch operation count per request (`100`).

## 10. Conflict Strategy
Concurrency model is ETag-based optimistic concurrency:
1. Reject stale write (`412 ETAG_MISMATCH`).
2. Client refetches latest document/etag.
3. Client rebases and retries.

Service does not perform multi-document transactions.

## 11. Context Assembly Behavior
- Bindings are read by ascending `read_priority`.
- Only fixed-path bindings are assembled by default.
- Templated bindings (for example `path_template`) are not auto-expanded in `context:assemble`.
- Caller loads templated documents on demand via direct document APIs.
- Event digests are retrieved via `events:search` as a separate API call.
- `max_docs` and `max_chars_total` budgets are enforced.
- Dropped bindings are surfaced in response metadata.

## 12. Retention and Deletion
Retention is defined per policy via `retention_rules`.

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

## 13. Error Model
Canonical status codes:
- `400` invalid request
- `403` write mode / policy restrictions
- `404` missing document or policy
- `412` ETag mismatch
- `422` validation or path-policy violations
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

## 14. Security Boundary
System-wide safety/compliance policy is outside user memory storage and not writable through memory APIs.

`mem.net` enforces structural safety (path/shape/size/concurrency/audit), not global policy reasoning.

## 15. Observability Requirements
Minimum telemetry:
- request latency by endpoint
- patch success/error counts (`412`, `422`, etc.)
- per-binding document size utilization
- event search latency
- retention/forget-user deletion counts

Audit records include actor, tenant/user, target path, ETag transition, reason, and evidence pointers.

## 16. Deployment Notes
- Provider selected via `MemNet:Provider` or `MEMNET_PROVIDER`.
- `filesystem` provider runs locally with no cloud dependencies.
- `azure` provider requires Azure SDK build flag and Azure configuration.
- If azure provider is selected without Azure SDK build flag, API returns `501 AZURE_PROVIDER_NOT_ENABLED`.
- Azure AI Search index provisioning is deployment/bootstrap responsibility (not runtime startup mutation).
- Bootstrap tool: `tools/MemNet.Bootstrap` with `--check` and `--apply`.
- Event index schema artifact: `infra/search/events-index.schema.json`.

## 17. Deferred Extensions
The following are intentionally deferred from v1 runtime core:
- dedicated compaction worker and compaction-specific config
- confidence-based write gating
- external schema registry/migration orchestration
- replay/reindex background orchestration
- backend composition abstraction (`IMemoryBackend`)

## 18. Acceptance Criteria (v1)
1. Policy-driven binding resolution and validation are enforced.
2. Live patch and replace operations require `If-Match` and enforce ETag semantics.
3. Context assembly returns deterministic base documents with budget handling.
4. Event write and search APIs are functional.
5. Retention and forget-user lifecycle endpoints are functional.
6. Audit trail is produced for mutating document operations.

## 19. Implementation Status
Current implementation satisfies v1 acceptance criteria and passes spec tests in `tests/MemNet.MemoryService.SpecTests`.
