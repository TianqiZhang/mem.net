# TASK_BOARD

## Status Legend

- `[ ]` not started
- `[-]` in progress
- `[x]` done

## Active Priorities

### Runtime and Providers

- [x] Publish Retrievo as NuGet package and replace relative project reference (Retrievo 0.2.0-preview.1)
- [ ] Execute env-gated live Azure integration runs (real tenant resources)
- [ ] Run end-to-end manual API checks with `MEMNET_PROVIDER=azure`

### Local Search Backend

- [x] Integrate Retrievo as local index/retrieval engine (`RetrievoEventStore`)
- [x] Add range filtering and contains filtering to Retrievo (Phase 1 enhancements)
- [x] Register `retrievo` provider in `MemoryBackendFactory`
- [x] Add RetrievoEventStore unit tests (18 tests covering search, filtering, cross-tenant isolation)
- [x] Code review and fix all CRITICAL/WARNING findings (composite key, case normalization, dispose race, UTC timestamps, cancellation token)
### Search and Lifecycle

- [ ] Implement background replay/reindex worker orchestration
- [ ] Add dedicated snapshot ingestion/store path beyond lifecycle cleanup hooks

### Quality and Release Readiness

- [x] Add NuGet package metadata/readmes for `MemNet.Client` and `MemNet.AgentMemory`
- [x] Add manual publish workflow for SDK packages to nuget.org
- [-] Close Phase 17 after full validation run (`dotnet test` + smoke + docs sync)
- [-] Close Phase 18 after CI passes with framework tests and coverage reporting

## Recently Completed


### Docs and Contract Clarity

- [x] Rewrite `README.md` to file-first model with migration guide
- [x] Rewrite `docs/specs/MEMORY_SERVICE_SPEC.md` as normative policy-free contract
- [x] Rewrite `docs/specs/SDK_SPEC.md` with file-like tools as primary SDK surface
- [x] Split task tracking into active board + archive log

### File-First v2 Boundary

- [x] Remove public namespace concept from service/API/SDK contracts
- [x] Lock canonical file route to `/files/{**path}`
- [x] Keep context assembly path-only (`files[]`)
- [x] Make mutation/event evidence opaque JSON across contracts
- [x] Add `files:list` API + SDK + sample usage

### Test Framework Migration

- [x] Add framework suites (`xUnit`) for unit + integration tests
- [x] Migrate core service and SDK coverage into framework suites
- [x] Keep spec runner as smoke-only parity
- [x] Add CI coverage artifact publication and threshold gate

## Deferred / Low Priority

- [ ] Optional compaction worker and compaction-specific config

## Archive

Historical phase-by-phase log is preserved at:

- `docs/archive/TASK_BOARD_ARCHIVE.md`
