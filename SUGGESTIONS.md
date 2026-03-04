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

### 3. Replace serialized lock in `RetrievoEventStore` with `ReaderWriterLockSlim`

**Where**: `RetrievoStores.cs` — `_writeLock` used in both `WriteAsync` and `QueryAsync`  
**Problem**: All queries are serialized behind the same `lock` as writes. Under concurrent load (multiple agents querying simultaneously), this becomes a bottleneck — every read waits for every other read and every write.  
**Fix**: Replace `object _writeLock` with `ReaderWriterLockSlim`:
- `WriteAsync`: `_rwLock.EnterWriteLock()` / `ExitWriteLock()`
- `QueryAsync`: `_rwLock.EnterReadLock()` / `ExitReadLock()`

This allows concurrent reads while still serializing writes. The `_digestCache` dictionary also needs to become a `ConcurrentDictionary` or be protected by the same lock scope.

### 4. Validate composite key components

**Where**: `RetrievoStores.cs` — `CompositeKey()` method (line 311–314)  
**Problem**: `CompositeKey` joins `tenantId/userId/eventId` with `/`. If any component contains `/`, keys will collide silently. Example: tenant `a/b` + user `c` = `a/b/c` = tenant `a` + user `b/c`.  
**Fix**: Either validate that components don't contain `/` (throw `ArgumentException`), or use a delimiter that's invalid in IDs (e.g., `\0` or a multi-char separator like `:::`).

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

### 7. Make `RebuildIndex()` async in `RetrievoEventStore`

**Where**: `RetrievoStores.cs` — `RebuildIndex()` called in constructor  
**Problem**: Reads all event JSON files from disk synchronously in the constructor. For a data root with thousands of events, this blocks startup for seconds. Constructor-time I/O is also an anti-pattern for DI containers.  
**Fix**: Convert to factory pattern or `IHostedService.StartAsync`:
```csharp
public static async Task<RetrievoEventStore> CreateAsync(StorageOptions options, CancellationToken ct)
```
Or defer index build to first query with lazy initialization.

---

## Medium (quality improvements)

### 8. Increase test coverage threshold from 40% to 60%

**Where**: `.github/workflows/ci.yml`  
**Problem**: 40% is a very low bar. The codebase already has 29 test files — actual coverage is likely higher than 40%. The threshold should reflect reality and prevent regression.  
**Fix**: Measure current coverage, then set threshold to `current - 5%` to prevent backsliding while leaving room for infrastructure code that's hard to unit test.

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

### 13. Add `IAsyncDisposable` to `RetrievoEventStore`

**Where**: `RetrievoStores.cs`  
**Problem**: `Dispose()` holds a lock and disposes the Retrievo index synchronously. If the index grows large, dispose could take non-trivial time. ASP.NET's DI container prefers `IAsyncDisposable` for graceful shutdown.

### 14. Add structured event metadata validation

**Where**: `MemoryCoordinator.cs` — event write path  
**Problem**: Event digests accept arbitrary `Keywords` and `ProjectIds` arrays with no size validation. An agent could write an event with 10,000 keywords, bloating the Retrievo index.  
**Fix**: Add configurable limits (e.g., max 50 keywords, max 20 project IDs, max 10KB digest text).

### 15. Add NuGet packages for Client and AgentMemory SDKs

**Where**: `MemNet.Client.csproj`, `MemNet.AgentMemory.csproj`  
**Problem**: Agent developers currently need to clone the repo and add project references. Publishing `MemNet.Client` and `MemNet.AgentMemory` to NuGet would dramatically lower the adoption barrier.  
**Fix**: Add NuGet packaging metadata to both `.csproj` files and publish. Follow the same pattern Retrievo used.

### 16. Add telemetry/metrics hooks

**Where**: `MemoryCoordinator.cs`  
**Problem**: No observability. In production, operators need to know request latency, error rates, cache hit rates, index size, etc.  
**Fix**: Add `System.Diagnostics.Metrics` counters and `ActivitySource` spans to `MemoryCoordinator`. These are zero-cost when no listener is attached (no external dependency needed).

### 17. Source Link for NuGet debugging

**Where**: `.csproj` files  
**Problem**: Same as Retrievo — when SDK packages are published, users debugging through mem.net code won't see source without Source Link.  
**Fix**: Add `Microsoft.SourceLink.GitHub` package and `<PublishRepositoryUrl>true</PublishRepositoryUrl>`.

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
