# TASK_BOARD

## Status Legend
- `[ ]` not started
- `[-]` in progress
- `[x]` done

## Phase 0 - Project Setup
- [x] Initialize solution and projects
- [x] Add repo docs (`README.md`, `AGENTS.md`, `TASK_BOARD.md`)
- [x] Configure offline-friendly NuGet behavior

## Phase 1 - Core Domain and Config
- [x] Define domain contracts (documents, envelopes, events, replay patches)
- [x] Implement policy loader (`policy.json`)
- [x] Implement validation engine (binding allowlist + required paths + size limits)
- [x] Implement ETag-based optimistic concurrency core services

## Phase 2 - Storage and Search Providers
- [x] Implement document store abstraction
- [x] Implement filesystem document store provider
- [x] Implement event store abstraction/provider
- [x] Implement in-memory event search provider
- [x] Implement audit store/provider

## Phase 3 - API Endpoints
- [x] `GET /documents/{namespace}/{path}`
- [x] `PATCH /documents/{namespace}/{path}`
- [x] `PUT /documents/{namespace}/{path}` (restricted)
- [x] `POST /context:assemble`
- [x] `POST /events`
- [x] `POST /events:search`
- [x] `POST /retention:apply`
- [x] `DELETE /memory` (forget-user)
- [x] Canonical error model and request tracing

## Phase 4 - Replay and Compaction Foundations
- [x] Replay patch ingestion contract
- [x] Conflict retry/rebase hook points
- [ ] Compaction worker (low priority, deferred from runtime core)

## Phase 5 - Testing and Quality
- [x] Build executable spec test harness
- [x] Add happy path API tests
- [x] Add negative tests (`412`, `422`, `404`)
- [x] Add context routing and budget tests
- [x] Add event search tests
- [x] Add retention and forget-user endpoint tests

## Phase 6 - Finalization
- [x] Run full build/tests
- [x] Review against acceptance criteria in spec
- [x] Polish docs and mark remaining gaps

## Phase 7 - Hardening and Providers
- [x] Add HTTP-level integration tests (real service process)
- [x] Add runtime configuration overrides for data/config roots
- [x] Add provider selection wiring (`filesystem` / `azure`)
- [x] Add Azure provider scaffolding behind store interfaces

## Phase 8 - Azure Provider Implementation
- [x] Verify exact Azure SDK usage against Microsoft docs (Blob, AI Search, Identity)
- [x] Add Azure SDK package references and options model
- [x] Implement `AzureBlobDocumentStore` with optimistic concurrency (`ETag` / `If-Match`)
- [x] Implement `AzureBlobAuditStore` writes to tenant/user audit blobs
- [x] Implement `AzureBlobEventStore` blob persistence
- [x] Implement Azure AI Search indexing on event writes (upsert)
- [x] Implement Azure AI Search query path for `events:search`
- [x] Add graceful fallback/error mapping for transient Azure failures

## Phase 9 - Azure Testing and Validation
- [x] Add integration check that azure provider returns `501` when SDK build flag is disabled
- [x] Add unit tests for Azure provider mapping and request shaping
- [x] Add provider-agnostic contract tests for document/event/audit stores
- [x] Add optional live Azure integration test harness (env-gated)
- [-] Validate `filesystem` and `azure` providers against same acceptance scenarios (shared spec harness in place; live azure run pending env)
- [ ] Run end-to-end manual API checks with `MEMNET_PROVIDER=azure`

## Phase 10 - Production Readiness
- [x] Add startup config validation for required Azure settings
- [x] Add retries/timeouts for Azure SDK calls with bounded policies
- [x] Add structured logs with tenant/user/path correlation fields
- [x] Document Azure setup in `README.md` (auth, env vars, index bootstrap)
- [x] Final review vs `MEMORY_SERVICE_SPEC.md` and close remaining gaps

## Phase 11 - First-Principles Simplification
- [x] Collapse runtime config to a single policy model (`policy.json`)
- [x] Move write/read validation constraints to binding-level policy fields
- [x] Remove runtime schema registry and policy interfaces
- [x] Remove request-time confidence gates from core mutation path
- [x] Defer compaction from runtime core to future background work
- [x] Rename API request selector fields from `profile_id` to `policy_id`
- [x] Remove project-specific routing semantics from `context:assemble`; keep assembly generic and fixed-path only
- [x] Update spec tests to match simplified policy behavior
- [x] Introduce `IMemoryBackend` abstraction and backend factory to simplify provider wiring

## Phase 12 - Structure Alignment
- [x] Reorganize source tree to match mental model (`Api`, `Application`, `Domain`, `Policy`, `Backends`)
- [x] Move default policy file to `src/MemNet.MemoryService/Policy/policy.json`
- [x] Update runtime default config root and test harness paths for policy location

## Phase 13 - Azure Bootstrap and Initialization
- [x] Add `tools/MemNet.Bootstrap` CLI for deployment-time initialization
- [x] Add idempotent Blob container provisioning (`--check` / `--apply`)
- [x] Add Azure AI Search index provisioning via source-controlled schema
- [x] Add schema artifact `infra/search/events-index.schema.json`
- [x] Add executable spec tests for bootstrap schema + CLI argument parsing
- [x] Update README/spec docs with bootstrap flow and deployment order

## Phase 14 - v2 Boundary Lock (Service/SDK)
- [x] Redefine `MEMORY_SERVICE_SPEC.md` to policy-free v2 target boundary
- [x] Redefine `SDK_SPEC.md` so policy/binding/slot validation is SDK-owned
- [x] Add explicit v1 -> v2 compatibility mapping in both specs

## Phase 15 - Service v2 Migration
- [x] Refactor runtime checks into service-core guards vs policy-dependent checks
- [x] Add v2 request contracts without `policy_id`/`binding_id`
- [x] Keep v1 request compatibility during migration window
- [x] Migrate `context:assemble` to explicit document refs
- [x] Migrate `retention:apply` to explicit retention settings
- [x] Update spec tests to cover both v1 compatibility and v2 native behavior

## Phase 16 - SDK Delivery
- [ ] Create solution projects for `MemNet.Client` and `MemNet.AgentMemory`
- [ ] Implement low-level v2 endpoint client with typed error mapping
- [ ] Implement ETag conflict helper (`UpdateWithRetryAsync`) with bounded retries
- [ ] Implement high-level `AgentMemory` facade (`PrepareTurnAsync`, `RecallAsync`, `RememberAsync`)
- [ ] Add SDK contract tests against local service harness
- [ ] Add SDK quickstart samples to `README.md`

## Residual Gaps (Post-Review)
- [ ] Execute env-gated live Azure integration runs (requires tenant resources/credentials)
- [ ] Implement full background replay/reindex worker orchestration (currently contracts + service hooks)
- [ ] Add dedicated snapshot ingestion/store path beyond lifecycle cleanup hooks

## Low-Priority Backlog
- [ ] Reintroduce compaction as optional background job with dedicated config
