# mem.net Memory Service Technical Specification

Project: `mem.net`  
Status: Draft for engineering review  
Version: 1.0  
Last Updated: February 14, 2026

## 1. Purpose
Build a shared, Azure-native memory service that multiple AI products can use for the same user identity. The service must keep memory durable, auditable, safe, and easy to evolve without hard-coding specific memory categories.

This specification defines the platform contract (storage, APIs, consistency, governance), plus a recommended starter memory profile for real agents.

## 2. Scope
### In scope
- Config-driven memory documents stored in Blob Storage.
- Config-driven schema validation, writable path controls, and retention.
- Conversation snapshots for replay extraction.
- Searchable event digests in Azure AI Search.
- Context assembly APIs for orchestrators.
- Live writes (agent-time) and replay writes (post-conversation).
- Multi-service support under unified `(tenant_id, user_id)`.

### Out of scope (v1)
- Temporal knowledge graph or ontology reasoning engine.
- Fully autonomous LLM policy management.
- End-user memory editing UI (can be added later).
- Cross-tenant federation.

## 3. Design Goals
1. Keep memory practical: small number of durable artifacts, predictable operations.
2. Keep service generic: no hard-coded memory types in core APIs.
3. Keep correctness high: strong validation, evidence-linked writes, conflict safety.
4. Keep operations simple: rebuildable indexes, clear retention/deletion, auditable changes.
5. Keep costs controlled: bounded document sizes and compaction.

## 4. Core Principles
1. Artifact-first: Blob documents are source of truth.
2. Config over convention: memory categories are profile-defined.
3. Progressive disclosure: inject only relevant memory at runtime.
4. Replay-first extraction: prefer snapshot replay for durable updates.
5. Least privilege writes: per-profile writable path allowlists.
6. Rebuildable search: indexes are derived, never authoritative.

## 5. Terminology
- Document: versioned JSON artifact stored in Blob (for example `user_dynamic.json`).
- Namespace: logical grouping of documents (for example `user`, `projects`, `crm`).
- Schema: JSON Schema used to validate a document kind/version.
- Profile: config bundle that maps an agent/product to allowed schemas, paths, and limits.
- Snapshot: encrypted blob of full conversation context used for replay.
- Event Digest: searchable summary record with pointers to evidence/snapshot.
- Patch Operation: RFC 6902 JSON Patch operation applied with optimistic concurrency.

## 6. Non-negotiable Boundary: System Policy vs User Memory
System-wide safety/compliance policy is not stored in user memory and is not writable through memory APIs.  
User-level guidance may be stored as memory, but it must be explicitly classified (for example `user_instructions`) and constrained by profile policy.

## 7. High-Level Architecture
### 7.1 Components
- Memory API (App Service or Functions):
  - document read/write/patch
  - context assembly
  - schema/path/size validation
  - ETag and idempotency enforcement
- Blob Storage (source of truth):
  - documents
  - snapshots
  - event records
  - audit records
- Azure AI Search (derived):
  - event search index
- Background workers (Functions + Queue/Service Bus):
  - replay extraction
  - compaction
  - reindex jobs
- Security dependencies:
  - Entra ID, managed identity, Key Vault, private networking as required

### 7.2 Storage layout (reference)
`/tenants/{tenant_id}/users/{user_id}/documents/{namespace}/{doc_name}.json`  
`/tenants/{tenant_id}/users/{user_id}/snapshots/{conversation_id}/{snapshot_id}.json`  
`/tenants/{tenant_id}/users/{user_id}/events/{event_id}.json`  
`/tenants/{tenant_id}/users/{user_id}/audit/{change_id}.json`

Note: layout is conventional, but document categories remain profile-configurable.

## 8. Configuration Model (Service-Generic)
The service behavior is driven by a profile registry, not hard-coded memory categories.

### 8.1 Schema Registry
Each schema is registered as:
- `schema_id` (for example `memory.user.dynamic`)
- `version` (for example `1.0.0`)
- JSON Schema body
- optional migration metadata

### 8.2 Profile Registry
A profile defines what an agent/product can read/write and how data is governed.

Example profile (abridged):
```json
{
  "profile_id": "project-copilot-v1",
  "document_bindings": [
    {
      "binding_id": "user_static",
      "namespace": "user",
      "path": "user_static.json",
      "schema_id": "memory.user.static",
      "schema_version": "1.0.0",
      "max_chars": 12000,
      "read_priority": 10,
      "write_mode": "restricted_patch"
    },
    {
      "binding_id": "user_dynamic",
      "namespace": "user",
      "path": "user_dynamic.json",
      "schema_id": "memory.user.dynamic",
      "schema_version": "1.0.0",
      "max_chars": 18000,
      "read_priority": 20,
      "write_mode": "restricted_patch"
    },
    {
      "binding_id": "project_doc",
      "namespace": "projects",
      "path_template": "{project_id}.json",
      "schema_id": "memory.project",
      "schema_version": "1.0.0",
      "max_chars": 32000,
      "read_priority": 30,
      "write_mode": "restricted_patch"
    }
  ],
  "writable_path_rules": {
    "user_dynamic": [
      "/preferences",
      "/durable_facts",
      "/pending_confirmations"
    ],
    "project_doc": [
      "/summary",
      "/facets",
      "/recent_notes"
    ]
  },
  "retention_rules": {
    "snapshots_days": 60,
    "events_days": 365,
    "audit_days": 365
  },
  "confidence_rules": {
    "min_confidence_for_durable_fact": 0.80,
    "min_confidence_for_auto_apply": 0.70
  },
  "compaction_rules": {
    "user_dynamic": {
      "max_preferences": 12,
      "max_durable_facts": 80,
      "max_pending_confirmations": 30
    },
    "project_doc": {
      "max_recent_notes": 30
    }
  }
}
```

## 9. Data Contracts
### 9.1 Common Document Envelope
All memory documents should include:
```json
{
  "doc_id": "uuid",
  "schema_id": "memory.user.dynamic",
  "schema_version": "1.0.0",
  "created_at": "2026-02-14T20:00:00Z",
  "updated_at": "2026-02-14T20:00:00Z",
  "updated_by": "service-or-job-id",
  "content": {}
}
```

Rationale: envelope supports migrations and observability without constraining content semantics.

### 9.2 Event Digest Record
```json
{
  "event_id": "evt_01...",
  "tenant_id": "t1",
  "user_id": "u1",
  "service_id": "assistant-a",
  "timestamp": "2026-02-14T20:00:00Z",
  "source_type": "chat",
  "digest": "User asked to split memory by contention and lifecycle.",
  "keywords": ["memory", "config", "conflict"],
  "project_ids": ["project-alpha"],
  "snapshot_uri": "blob://...",
  "evidence": {
    "message_ids": ["m12", "m13"],
    "span": { "start": 12, "end": 13 }
  }
}
```

### 9.3 Replay Patch Record (internal)
```json
{
  "replay_id": "rpl_01...",
  "target_binding_id": "user_dynamic",
  "target_path": "user_dynamic.json",
  "base_etag": "\"0x8DE...\"",
  "ops": [
    { "op": "add", "path": "/content/preferences/-", "value": "Prefer concise architecture diagrams." }
  ],
  "confidence": 0.82,
  "evidence": {
    "snapshot_uri": "blob://...",
    "message_ids": ["m18"]
  },
  "idempotency_key": "replay-rpl_01-user_dynamic"
}
```

## 10. API Specification
All APIs are server-to-server and require authenticated service identity.

### 10.1 Get Document
`GET /v1/tenants/{tenant_id}/users/{user_id}/documents/{namespace}/{path}`

`path` is a URL-encoded blob-relative path inside the namespace (for example `user_static.json` or `project-alpha.json`).

Response:
```json
{
  "etag": "\"0x8DE...\"",
  "document": { "...": "..." }
}
```

### 10.2 Patch Document
`PATCH /v1/tenants/{tenant_id}/users/{user_id}/documents/{namespace}/{path}`

Headers:
- `If-Match: <etag>` (required)
- `Idempotency-Key: <key>` (required)

Request:
```json
{
  "profile_id": "project-copilot-v1",
  "binding_id": "user_dynamic",
  "ops": [
    { "op": "replace", "path": "/content/preferences/0", "value": "Use concise, direct answers." }
  ],
  "reason": "live_update",
  "evidence": {
    "conversation_id": "c_123",
    "message_ids": ["m1"]
  }
}
```

Behavior:
- Validates profile binding, schema, and writable paths.
- Enforces size and field limits after patch application.
- Returns `412 Precondition Failed` on ETag mismatch.
- Returns `409 Conflict` for idempotency-key reuse with different payload.

### 10.3 Replace Document (restricted)
`PUT /v1/tenants/{tenant_id}/users/{user_id}/documents/{namespace}/{path}`

Use for migration/admin flows, usually not for direct LLM writes.

### 10.4 Assemble Context
`POST /v1/tenants/{tenant_id}/users/{user_id}/context:assemble`

Request:
```json
{
  "profile_id": "project-copilot-v1",
  "conversation_hint": {
    "text": "Need help with project alpha retrieval latency",
    "project_id": null
  },
  "max_docs": 4
}
```

Response:
```json
{
  "selected_project_id": "project-alpha",
  "documents": [
    {
      "binding_id": "user_static",
      "namespace": "user",
      "path": "user_static.json",
      "etag": "\"0x8...\"",
      "document": { "...": "..." }
    }
  ],
  "routing_debug": {
    "deterministic_score": 0.93,
    "semantic_score": 0.61,
    "reason": "alias_match"
  }
}
```

### 10.5 Write Event Digest
`POST /v1/tenants/{tenant_id}/users/{user_id}/events`

Writes event blob and upserts search index document.

### 10.6 Search Events
`POST /v1/tenants/{tenant_id}/users/{user_id}/events:search`

Request supports filters: time range, service, project, source_type, topK.

### 10.7 Error Model
Canonical status codes:
- `400 Bad Request`: malformed payload or invalid operation syntax.
- `401/403`: authentication or authorization failure.
- `404 Not Found`: missing document/binding.
- `409 Conflict`: idempotency key reused with non-identical payload.
- `412 Precondition Failed`: ETag mismatch.
- `422 Unprocessable Entity`: schema validation, path policy, or limit violation.
- `429 Too Many Requests`: per-user or per-service quota exceeded.
- `500/503`: transient server/dependency failures.

Error payload shape:
```json
{
  "error": {
    "code": "ETAG_MISMATCH",
    "message": "If-Match does not match latest document version.",
    "request_id": "req_01...",
    "details": {
      "latest_etag": "\"0x8DF...\""
    }
  }
}
```

## 11. Runtime Read Path
At conversation start:
1. Orchestrator calls `context:assemble` with profile and hint.
2. Service resolves relevant docs in priority order.
3. Service returns selected documents with ETags.
4. Orchestrator injects docs into system context as memory artifacts.

Routing precedence:
1. Explicit `project_id` if provided and valid.
2. Deterministic alias/keyword match from routing document.
3. Semantic disambiguation only when deterministic routing is ambiguous.
4. No project document when confidence too low.

Token budget policy:
- `context:assemble` should support optional `max_chars_total` and `max_docs`.
- If over budget, service drops lowest-priority bindings first.
- Service should return `dropped_bindings` metadata so orchestration logs explain omissions.

## 12. Runtime Write Path (Live)
1. Agent proposes patch ops.
2. Orchestrator sends patch with ETag + idempotency key.
3. Memory API validates and applies atomically.
4. API writes audit record and returns new ETag.

Write safeguards:
- Path allowlist per binding.
- Denylist support for sensitive fields.
- Max operation count per patch request.
- Confidence threshold gates for durable sections.
- Optional `pending_confirmation` redirection when confidence is insufficient.

## 13. Post-Conversation Replay Flow
1. Conversation snapshot stored as encrypted blob.
2. Replay worker loads snapshot + current documents + profile config.
3. Worker emits:
   - event digest
   - one or more patch requests with evidence and confidence
4. API applies patches with If-Match.
5. On `412`, worker refetches and rebases once, then retries.
6. If second conflict remains, worker stores unresolved patch for manual/async retry.

Replay output contract must remain structured (digest + patch + evidence + confidence), not free-form text.

## 14. Conflict Strategy
Use optimistic concurrency per document via Blob ETag.

Conflict handling algorithm:
1. Reject stale write (`412`).
2. Client fetches latest document + ETag.
3. Client rebases original ops to latest version.
4. Retry once with same idempotency key lineage.
5. Escalate unresolved conflicts to retry queue with backoff.

Reasoning: simple, cloud-native, and sufficient for multi-service writers.

## 15. Compaction and Size Management
Compaction is profile-configurable and runs periodically.

Actions:
- Trim low-value `recent_notes` first.
- Merge redundant preference/fact entries.
- Move granular history to event digests only.
- Preserve high-confidence durable facts with freshness metadata.

Required limits:
- `max_chars` per document binding.
- max array lengths per logical section.
- max total patch operations per request.

## 16. Security and Privacy
### 16.1 AuthN/AuthZ
- Service-to-service auth with Entra ID.
- Tenant-bound authorization checks on every call.
- No cross-tenant reads/writes.

### 16.2 Encryption
- Encryption at rest for Blob and Search.
- Snapshot payloads encrypted and access-controlled.
- Optional CMK via Key Vault for regulated tenants.

### 16.3 Data minimization
- Store only durable and high-value memory.
- Prefer digests + evidence pointers over verbose duplication.

## 17. Retention and Deletion
Retention is profile-configurable with organization-level ceilings.

Recommended defaults:
- snapshots: 30-90 days
- events: 180-365 days
- audit: >= 365 days (subject to policy)
- memory documents: long-lived until user/tenant deletion

Forget flow (`delete user memory`) must remove:
- all documents
- snapshots
- event blobs
- search index docs
- audit records (unless legal hold applies)

## 18. Safety Model
The memory service enforces structural safety, not global policy reasoning.

Structural safety includes:
- allowed schemas only
- writable path controls
- confidence gates
- idempotency and auditability

Global policy enforcement remains in orchestrator/system prompts and policy services.

## 19. Observability and SLOs
### 19.1 Mandatory telemetry
- request latency by endpoint
- patch success/412/validation failure rates
- idempotency conflict rate
- per-binding size utilization
- replay apply success rate
- search latency and hit quality proxy metrics

### 19.2 Audit record fields
- actor service/job identity
- tenant/user
- timestamp
- target document binding/path
- pre/post ETag
- patch ops hash
- evidence pointers
- reason code (`live_update`, `replay_update`, `admin_migration`)

### 19.3 Suggested SLO targets (initial)
- p95 read latency < 150 ms (document cache warm)
- p95 patch latency < 300 ms (excluding conflict retries)
- replay success within 5 minutes for 99% of completed conversations

### 19.4 Operational Alerts (minimum)
- sustained `412` rate above threshold for any binding
- schema validation failure spikes
- replay backlog age above SLA
- search indexing lag above SLA

## 20. Deployment and Operations
- Blue/green rollout for API and workers.
- Schema registry supports additive versioning.
- Migrations use `PUT` with privileged role and migration audit trail.
- Reindex job can rebuild search from event blobs.

## 21. Naming and Migration Guidance
Current naming like `global.json` is semantically ambiguous. Prefer profile-configurable names, with recommended conventions:
- `user_static.json`
- `user_dynamic.json`
- `projects/{project_id}.json`

Migration strategy:
1. Introduce profile with new bindings.
2. Backfill from old documents.
3. Dual-read for one release window.
4. Cut write traffic to new paths.
5. Decommission legacy file after verification.

## 22. Starter Profile (Recommended, Not Hard-Coded)
This is a sample for real agents. It is guidance only.

### 22.1 Suggested logical sections
- `profile` (stable identity/context)
- `preferences` (response/work style)
- `durable_facts` (high-confidence, cross-session facts)
- `pending_confirmations` (low-confidence candidate memories)
- `projects_index` (routing metadata)

### 22.2 Admission rules for durable facts
A fact must satisfy all:
1. useful beyond current session
2. likely stable over time
3. confidence >= configured threshold
4. has evidence pointer
5. passes sensitivity rules

### 22.3 Freshness rules
- add `last_verified_at`
- optional `expires_at` or TTL class
- stale facts can downgrade to `pending_confirmations`

## 23. Risks and Mitigations
1. Prompt-injected bad memory writes.
- Mitigation: strict path controls + confidence gates + pending confirmations + audit.

2. Silent factual drift over time.
- Mitigation: evidence-linked writes + freshness metadata + periodic compaction.

3. Multi-writer conflicts.
- Mitigation: ETag + deterministic retry/rebase + idempotency keys.

4. Oversized documents reducing quality.
- Mitigation: strict budgets + compaction + move detail to events.

5. Weak event recall quality.
- Mitigation: index digest + keywords/entities + pointers to snapshots.

6. Privacy complexity with snapshots.
- Mitigation: short retention + encrypted storage + complete delete flow.

## 24. Open Decisions for Engineering Review
1. Should replay unresolved conflicts go to human review queue or automated delayed retries only?
2. Do we require confidence scoring from all writer clients, or only replay workers?
3. What is the default retention profile for enterprise tenants with legal hold?
4. Should `context:assemble` return routing debug data in production or behind debug flag?
5. Which fields should be indexed as filterable/facetable in Azure AI Search v1?

## 25. Acceptance Criteria (v1)
1. Service supports profile-driven schema validation and writable path policies.
2. Live patching uses required ETag and idempotency semantics.
3. Replay pipeline writes structured digests and evidence-linked patches.
4. Context assembly works without exposing direct memory search tools to the LLM.
5. Retention and forget-user flows are implemented and tested.
6. Audit logs allow full reconstruction of memory mutations.

## 26. Final Recommendation
Proceed with a configurable memory platform and ship one starter profile first. Avoid hard-coding memory categories in service logic. Keep memory artifacts compact and enforce strict governance at write time. Expand profile variants only after usage data validates the need.
