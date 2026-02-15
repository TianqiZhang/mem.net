using System.Text.Json;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var appRoot = builder.Environment.ContentRootPath;
var options = new StorageOptions
{
    DataRoot = Path.Combine(appRoot, "data"),
    ConfigRoot = Path.Combine(appRoot, "config")
};

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IDocumentStore, FileDocumentStore>();
builder.Services.AddSingleton<IEventStore, FileEventStore>();
builder.Services.AddSingleton<IAuditStore, FileAuditStore>();
builder.Services.AddSingleton<IProfileRegistryProvider, FileRegistryProvider>();
builder.Services.AddSingleton<ISchemaRegistryProvider, FileRegistryProvider>(sp => (FileRegistryProvider)sp.GetRequiredService<IProfileRegistryProvider>());
builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddSingleton<MemoryCoordinator>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddSingleton<CompactionService>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ApiException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        context.Response.ContentType = "application/json";
        var payload = new ErrorEnvelope(new ApiError(ex.Code, ex.Message, context.TraceIdentifier, ex.Details));
        await context.Response.WriteAsJsonAsync(payload);
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        var payload = new ErrorEnvelope(new ApiError("UNHANDLED_ERROR", ex.Message, context.TraceIdentifier));
        await context.Response.WriteAsJsonAsync(payload);
    }
});

app.MapGet("/", () => Results.Ok(new { service = "mem.net", status = "ok" }));

app.MapGet("/v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{**path}", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromRoute(Name = "namespace")] string namespaceName,
    [FromRoute] string path,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var decodedPath = Uri.UnescapeDataString(path);
    var key = new DocumentKey(tenantId, userId, namespaceName, decodedPath);
    var doc = await coordinator.GetDocumentAsync(key, cancellationToken);
    return Results.Ok(new
    {
        etag = doc.ETag,
        document = doc.Envelope
    });
});

app.MapPatch("/v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{**path}", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromRoute(Name = "namespace")] string namespaceName,
    [FromRoute] string path,
    [FromBody] PatchDocumentRequest request,
    HttpContext httpContext,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var decodedPath = Uri.UnescapeDataString(path);
    var key = new DocumentKey(tenantId, userId, namespaceName, decodedPath);
    var ifMatch = httpContext.Request.Headers.IfMatch.ToString();
    var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
    var actor = httpContext.Request.Headers["X-Service-Id"].ToString();
    if (string.IsNullOrWhiteSpace(actor))
    {
        actor = "unknown-service";
    }

    var result = await coordinator.PatchDocumentAsync(key, request, ifMatch, idempotencyKey, actor, cancellationToken);
    return Results.Ok(new
    {
        etag = result.ETag,
        document = result.Document,
        idempotency_replay = result.IdempotencyReplay
    });
});

app.MapPut("/v1/tenants/{tenantId}/users/{userId}/documents/{namespace}/{**path}", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromRoute(Name = "namespace")] string namespaceName,
    [FromRoute] string path,
    [FromBody] ReplaceDocumentRequest request,
    HttpContext httpContext,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var decodedPath = Uri.UnescapeDataString(path);
    var key = new DocumentKey(tenantId, userId, namespaceName, decodedPath);
    var ifMatch = httpContext.Request.Headers.IfMatch.ToString();
    var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
    var actor = httpContext.Request.Headers["X-Service-Id"].ToString();
    if (string.IsNullOrWhiteSpace(actor))
    {
        actor = "unknown-service";
    }

    var result = await coordinator.ReplaceDocumentAsync(key, request, ifMatch, idempotencyKey, actor, cancellationToken);
    return Results.Ok(new
    {
        etag = result.ETag,
        document = result.Document,
        idempotency_replay = result.IdempotencyReplay
    });
});

app.MapPost("/v1/tenants/{tenantId}/users/{userId}/context:assemble", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromBody] AssembleContextRequest request,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.AssembleContextAsync(tenantId, userId, request, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/v1/tenants/{tenantId}/users/{userId}/events", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromBody] WriteEventRequest request,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(request.Event.TenantId, tenantId, StringComparison.Ordinal)
        || !string.Equals(request.Event.UserId, userId, StringComparison.Ordinal))
    {
        throw new ApiException(StatusCodes.Status422UnprocessableEntity, "EVENT_SCOPE_MISMATCH", "Event tenant/user does not match route scope.");
    }

    await coordinator.WriteEventAsync(request.Event, cancellationToken);
    return Results.Accepted();
});

app.MapPost("/v1/tenants/{tenantId}/users/{userId}/events:search", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromBody] SearchEventsRequest request,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.SearchEventsAsync(tenantId, userId, request, cancellationToken);
    return Results.Ok(result);
});

app.Run();

public partial class Program;
