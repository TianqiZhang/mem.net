# TASK_BOARD

## Status Legend

- `[ ]` not started
- `[-]` in progress
- `[x]` done

## Active Priorities

### Runtime and Providers

- [ ] Execute env-gated live Azure integration runs (real tenant resources)
- [ ] Run end-to-end manual API checks with `MEMNET_PROVIDER=azure`

### Search and Lifecycle

- [ ] Implement background replay/reindex worker orchestration
- [ ] Add dedicated snapshot ingestion/store path beyond lifecycle cleanup hooks

### Quality and Release Readiness

- [-] Close Phase 17 after full validation run (`dotnet test` + smoke + docs sync)
- [-] Close Phase 18 after CI passes with framework tests and coverage reporting

## Recently Completed

### Docs and Contract Clarity

- [x] Rewrite `README.md` to file-first model with migration guide
- [x] Rewrite `MEMORY_SERVICE_SPEC.md` as normative policy-free contract
- [x] Rewrite `SDK_SPEC.md` with file-like tools as primary SDK surface
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
