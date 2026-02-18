# Test Parity Checklist (Phase 18)

This checklist maps legacy executable spec coverage to the new framework-based test suites.

## Service Runtime Coverage

- [x] File API contracts (`GET/PATCH/PUT /files/{path}`)  
  Framework suite: `tests/MemNet.MemoryService.IntegrationTests/FileApiContractTests.cs`
- [x] Context assembly API behavior  
  Framework suite: `tests/MemNet.MemoryService.IntegrationTests/ContextEventsLifecycleApiTests.cs`
- [x] Event write/search API behavior  
  Framework suite: `tests/MemNet.MemoryService.IntegrationTests/ContextEventsLifecycleApiTests.cs`
- [x] Lifecycle APIs (retention + forget-user)  
  Framework suite: `tests/MemNet.MemoryService.IntegrationTests/ContextEventsLifecycleApiTests.cs`
- [x] Patch engine behavior and validation  
  Framework suite: `tests/MemNet.MemoryService.UnitTests/JsonPatchEngineTests.cs`
- [x] Coordinator validation/error mapping + deterministic text edit paths  
  Framework suite: `tests/MemNet.MemoryService.UnitTests/MemoryCoordinatorValidationTests.cs`

## SDK Coverage

- [x] `MemNet.Client` file/context/event flows  
  Framework suite: `tests/MemNet.Sdk.IntegrationTests/MemNetClientIntegrationTests.cs`
- [x] `MemNet.Client` typed API error mapping  
  Framework suite: `tests/MemNet.Sdk.UnitTests/MemNetClientRetryAndErrorTests.cs`
- [x] `UpdateWithRetryAsync` conflict retry semantics  
  Framework suites: `tests/MemNet.Sdk.UnitTests/MemNetClientRetryAndErrorTests.cs`, `tests/MemNet.Sdk.IntegrationTests/MemNetClientIntegrationTests.cs`
- [x] `MemNet.AgentMemory` prepare-turn + file-tool flows  
  Framework suite: `tests/MemNet.Sdk.IntegrationTests/AgentMemoryIntegrationTests.cs`

## Remaining Smoke-Only Coverage

- [x] Out-of-process service/runtime wiring smoke  
  Runner: `tests/MemNet.MemoryService.SpecTests`
- [x] Bootstrap/search schema and Azure-shape smoke checks  
  Runner: `tests/MemNet.MemoryService.SpecTests`
- [x] Optional slot/policy helper compatibility smoke (env-gated)  
  Runner flag: `MEMNET_RUN_OPTIONAL_SDK_TESTS=1`
