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
