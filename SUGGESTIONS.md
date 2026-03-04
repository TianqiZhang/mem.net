# mem.net — Suggestions

> Based on a first-principles code audit of every source file, test file, spec, and public contract. Organized by priority.

---

## Critical (fix before stable v1)

### 1. ~~Update README: Retrievo is now a NuGet package~~ ✅ DONE

**Where**: README.md line 238  
**Problem**: README still says *"Once Retrievo is published as a NuGet package, this sibling requirement will be removed."* Retrievo is now published. The sibling project reference instruction is outdated and will confuse users.  
**Fix**: Replace the sibling layout requirement with:
```
dotnet add package Retrievo --prerelease
```
**Fix**: ~~Replace the sibling layout requirement.~~ **Fixed**: README updated to state Retrievo is a NuGet dependency (0.2.0-preview.1) installed automatically via `dotnet restore`. Sibling directory layout requirement removed.

### 2. ~~Update TASK_BOARD: Retrievo NuGet task is done~~ ✅ DONE

**Where**: `docs/project/TASK_BOARD.md` line 13  
**Problem**: *"Publish Retrievo as NuGet package and replace relative project reference"* is still listed as `[ ]` not started. It's done.  
**Fix**: ~~Mark it `[x]` and move to Recently Completed.~~ **Fixed**: Marked `[x]` with version annotation (Retrievo 0.2.0-preview.1) in TASK_BOARD.md.

---

## High (strongly recommended)

### 3. ~~Replace serialized lock in `RetrievoEventStore` with `ReaderWriterLockSlim`~~ ✅ DONE

**Where**: `RetrievoStores.cs` — `_writeLock` used in both `WriteAsync` and `QueryAsync`  
**Problem**: All queries are serialized behind the same `lock` as writes. Under concurrent load (multiple agents querying simultaneously), this becomes a bottleneck — every read waits for every other read and every write.  
**Fix**: ~~Replace `object _writeLock` with `ReaderWriterLockSlim`.~~ **Fixed**: Replaced `object _writeLock` with `ReaderWriterLockSlim`. `WriteAsync` uses `EnterWriteLock/ExitWriteLock`, `QueryAsync` uses `EnterReadLock/ExitReadLock`. `_digestCache` is protected by the same lock scope.

### 4. ~~Validate composite key components~~ ✅ DONE

**Where**: `RetrievoStores.cs` — `CompositeKey()` method (line 311–314)  
**Problem**: `CompositeKey` joins `tenantId/userId/eventId` with `/`. If any component contains `/`, keys will collide silently. Example: tenant `a/b` + user `c` = `a/b/c` = tenant `a` + user `b/c`.  
**Fix**: ~~Validate or change delimiter.~~ **Fixed**: Changed delimiter from `/` to `\0` (null char) which is impossible in user-supplied strings. Added `ValidateKeyComponent()` that throws `ArgumentException` if any component contains `\0`.

### 5. Add OpenAPI schema export

**Where**: `Program.cs` (Minimal API)  
**Problem**: The REST API is well-designed but has no machine-readable schema. Agent framework authors who want to auto-generate clients for non-.NET languages have to read source code.  
**Fix**: Add `Microsoft.AspNetCore.OpenApi` (ships with .NET 8) and `builder.Services.AddOpenApi()`. Minimal API endpoints already have typed request/response — OpenAPI generation is nearly free. Consider also adding a Swagger UI endpoint for development.

### 6. Add Docker support

**Where**: Repository root  
**Problem**: No `Dockerfile` or `docker-compose.yml`. Running mem.net requires .NET 8 SDK installed locally. For agent developers who just want a memory service running, Docker is the expected deployment path.  
**Fix**: Add a minimal multi-stage Dockerfile:
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/MemNet.MemoryService -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "MemNet.MemoryService.dll"]
```
Add a `docker-compose.yml` for one-command startup with volume mount for `MEMNET_DATA_ROOT`.

### 7. ~~Make `RebuildIndex()` async in `RetrievoEventStore`~~ ✅ DONE

**Where**: `RetrievoStores.cs` — `RebuildIndex()` called in constructor  
**Problem**: Reads all event JSON files from disk synchronously in the constructor. For a data root with thousands of events, this blocks startup for seconds. Constructor-time I/O is also an anti-pattern for DI containers.  
**Fix**: ~~Convert to factory pattern.~~ **Fixed**: Constructor made private. Added `static async Task<RetrievoEventStore> CreateAsync(StorageOptions, CancellationToken)` factory method. `RebuildIndex()` → `static async RebuildIndexAsync()` using `File.ReadAllTextAsync` with cancellation support. DI registration updated to use factory lambda.

---

## Medium (quality improvements)

### 8. ~~Increase test coverage threshold from 40% to 60%~~ ✅ DONE (raised to 45%)

**Where**: `.github/workflows/ci.yml`  
**Problem**: 40% is a very low bar. The codebase already has 29 test files — actual coverage is likely higher than 40%. The threshold should reflect reality and prevent regression.  
**Fix**: ~~Measure current coverage, then set threshold.~~ **Fixed**: Measured weighted line coverage at 46.5%. Raised `COVERAGE_THRESHOLD` from `0.40` to `0.45` in `.github/workflows/ci.yml`. Conservative bump to prevent backsliding while leaving room for infrastructure code.

### 9. Add health check with backend connectivity validation

**Where**: `Program.cs` — health endpoint  
**Problem**: Current health check returns `{"status": "ok"}` unconditionally. It doesn't validate that the configured backend (filesystem directory exists, Azure connection works, Retrievo index is initialized) is actually functional.  
**Fix**: Add a `/health/ready` endpoint that verifies backend connectivity. Keep the existing `/` as a lightweight liveness probe.

### 10. Add request/response logging middleware

**Where**: `Program.cs`  
**Problem**: No request logging. When debugging agent integration issues, there's no visibility into what requests the service is receiving.  
**Fix**: Add `app.UseHttpLogging()` with configurable verbosity. For production, log method + path + status + latency. For development, log request/response bodies.

### 11. Add rate limiting / request size limits

**Where**: `Program.cs`  
**Problem**: No request size limits. A malicious or buggy agent could POST a 100MB event digest or a document with unlimited patch operations. No rate limiting on mutations.  
**Fix**: Add `app.UseRequestBodySizeLimit()` and consider `Microsoft.AspNetCore.RateLimiting` for mutation endpoints. Even permissive limits (10MB body, 100 req/s) prevent runaway scenarios.

### 12. Formalize error response contract in README

**Where**: README.md  
**Problem**: The SDK spec documents the error envelope format, but the README (which most users read first) doesn't show error response shapes. Users integrating from non-.NET languages need to know the error contract.  
**Fix**: Add an "Error Responses" section to README showing the standard envelope:
```json
{
  "error": {
    "code": "ETAG_MISMATCH",
    "message": "...",
    "request_id": "..."
  }
}
```

---

## Low (nice to have)

### 13. ~~Add `IAsyncDisposable` to `RetrievoEventStore`~~ ✅ DONE

**Where**: `RetrievoStores.cs`  
**Fix**: ~~Add IAsyncDisposable.~~ **Fixed**: `RetrievoEventStore` now implements `IAsyncDisposable`. `DisposeAsync()` delegates to `Dispose()`. `Dispose()` uses double-check locking with `ReaderWriterLockSlim` and disposes the lock after releasing.

### 14. ~~Add structured event metadata validation~~ ✅ DONE

**Where**: `MemoryCoordinator.cs` — event write path  
**Problem**: Event digests accept arbitrary `Keywords` and `ProjectIds` arrays with no size validation. An agent could write an event with 10,000 keywords, bloating the Retrievo index.  
**Fix**: ~~Add configurable limits.~~ **Fixed**: Added constants `MaxKeywords=50`, `MaxProjectIds=20`, `MaxDigestChars=10_000` in `MemoryCoordinator.cs`. Guard.True checks in `WriteEventAsync` throw 422 with `EVENT_METADATA_TOO_LARGE`. Three negative unit tests added.

### 15. Add NuGet packages for Client and AgentMemory SDKs

**Where**: `MemNet.Client.csproj`, `MemNet.AgentMemory.csproj`  
**Problem**: Agent developers currently need to clone the repo and add project references. Publishing `MemNet.Client` and `MemNet.AgentMemory` to NuGet would dramatically lower the adoption barrier.  
**Fix**: Add NuGet packaging metadata to both `.csproj` files and publish. Follow the same pattern Retrievo used.

### 16. Add telemetry/metrics hooks

**Where**: `MemoryCoordinator.cs`  
**Problem**: No observability. In production, operators need to know request latency, error rates, cache hit rates, index size, etc.  
**Fix**: Add `System.Diagnostics.Metrics` counters and `ActivitySource` spans to `MemoryCoordinator`. These are zero-cost when no listener is attached (no external dependency needed).

### 17. ~~Source Link for NuGet debugging~~ ✅ DONE

**Where**: `.csproj` files  
**Problem**: Same as Retrievo — when SDK packages are published, users debugging through mem.net code won't see source without Source Link.  
**Fix**: ~~Add Source Link package.~~ **Fixed**: Added `Microsoft.SourceLink.GitHub` v8.0.0 with `PublishRepositoryUrl`, `EmbedUntrackedSources`, `IncludeSymbols`, `SymbolPackageFormat=snupkg` to all 3 src/ csproj files.

---

## What's Already Great (don't change)

- **File-first mental model** — simple, debuggable, no hidden state
- **ETag optimistic concurrency on every mutation** — correct distributed systems pattern
- **SHA256-based ETags** — deterministic, content-addressable
- **Filesystem as source of truth, search index as derived state** — survives crashes, enables backend swaps
- **Clean backend abstraction** — Retrievo only swaps `IEventStore`, everything else stays filesystem
- **Retrievo knows nothing about mem.net** — zero reverse coupling
- **JSON Patch with correct RFC 6901 tokenization** — `~1` before `~0` decode order is right
- **Client retry with exponential backoff + jitter** — production-grade
- **Agent SDK slot/policy system** — powerful without being mandatory
- **Formal spec document** (`MEMORY_SERVICE_SPEC.md`) — rare and valuable for an early-stage project
- **Azure dependencies are build-flag gated** — doesn't bloat the default build
- **Honest coverage threshold** — 40% is low but real; no inflated metrics
