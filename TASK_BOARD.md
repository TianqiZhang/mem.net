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
- [x] Implement schema/profile registry loaders
- [x] Implement validation engine (schema + writable paths + limits + confidence)
- [x] Implement ETag and idempotency core services

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
- [x] Canonical error model and request tracing

## Phase 4 - Replay and Compaction Foundations
- [x] Replay patch ingestion contract
- [x] Conflict retry/rebase hook points
- [x] Compaction service scaffolding

## Phase 5 - Testing and Quality
- [x] Build executable spec test harness
- [x] Add happy path API tests
- [x] Add negative tests (`412`, `409`, `422`, `404`)
- [x] Add context routing and budget tests
- [x] Add event search tests

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
- [-] Verify exact Azure SDK usage against Microsoft docs (Blob, AI Search, Identity)
- [ ] Add Azure SDK package references and options model
- [ ] Implement `AzureBlobDocumentStore` with optimistic concurrency (`ETag` / `If-Match`)
- [ ] Implement `AzureBlobAuditStore` writes to tenant/user audit blobs
- [ ] Implement `AzureBlobEventStore` blob persistence
- [ ] Implement Azure AI Search indexing on event writes (upsert)
- [ ] Implement Azure AI Search query path for `events:search`
- [ ] Add graceful fallback/error mapping for transient Azure failures

## Phase 9 - Azure Testing and Validation
- [ ] Add unit tests for Azure provider mapping and request shaping
- [ ] Add provider-agnostic contract tests for document/event/audit stores
- [ ] Add optional live Azure integration test harness (env-gated)
- [ ] Validate `filesystem` and `azure` providers against same acceptance scenarios
- [ ] Run end-to-end manual API checks with `MEMNET_PROVIDER=azure`

## Phase 10 - Production Readiness
- [ ] Add startup config validation for required Azure settings
- [ ] Add retries/timeouts for Azure SDK calls with bounded policies
- [ ] Add structured logs with tenant/user/path correlation fields
- [ ] Document Azure setup in `README.md` (auth, env vars, index bootstrap)
- [ ] Final review vs `MEMORY_SERVICE_SPEC.md` and close remaining gaps
