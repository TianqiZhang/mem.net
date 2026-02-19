using System.Text.Json;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var appRoot = builder.Environment.ContentRootPath;
var configuredDataRoot = builder.Configuration["MemNet:DataRoot"] ?? Environment.GetEnvironmentVariable("MEMNET_DATA_ROOT");
var configuredProvider = builder.Configuration["MemNet:Provider"] ?? Environment.GetEnvironmentVariable("MEMNET_PROVIDER");

var options = new StorageOptions
{
    DataRoot = string.IsNullOrWhiteSpace(configuredDataRoot) ? Path.Combine(appRoot, "data") : configuredDataRoot
};

var provider = string.IsNullOrWhiteSpace(configuredProvider)
    ? "filesystem"
    : configuredProvider.Trim().ToLowerInvariant();

builder.Services.AddSingleton(options);
var backend = MemoryBackendFactory.Create(provider);
builder.Services.AddSingleton<IMemoryBackend>(backend);
backend.RegisterServices(builder.Services, builder.Configuration);

builder.Services.AddSingleton<MemoryCoordinator>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddSingleton<DataLifecycleService>();

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

app.MapGet("/v1/tenants/{tenantId}/users/{userId}/files:list", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromQuery] string? prefix,
    [FromQuery] int? limit,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.ListFilesAsync(
        tenantId,
        userId,
        new ListFilesRequest(prefix, limit),
        cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/v1/tenants/{tenantId}/users/{userId}/files/{**path}", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromRoute] string path,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var key = BuildDocumentKey(tenantId, userId, path);
    var doc = await coordinator.GetDocumentAsync(key, cancellationToken);
    return Results.Ok(new
    {
        etag = doc.ETag,
        document = doc.Envelope
    });
});

app.MapPatch("/v1/tenants/{tenantId}/users/{userId}/files/{**path}", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromRoute] string path,
    [FromBody] PatchDocumentRequest request,
    HttpContext httpContext,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var key = BuildDocumentKey(tenantId, userId, path);
    var ifMatch = httpContext.Request.Headers.IfMatch.ToString();
    var actor = httpContext.Request.Headers["X-Service-Id"].ToString();
    if (string.IsNullOrWhiteSpace(actor))
    {
        actor = "unknown-service";
    }

    var result = await coordinator.PatchDocumentAsync(key, request, ifMatch, actor, cancellationToken);
    return Results.Ok(new
    {
        etag = result.ETag,
        document = result.Document
    });
});

app.MapPut("/v1/tenants/{tenantId}/users/{userId}/files/{**path}", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromRoute] string path,
    [FromBody] ReplaceDocumentRequest request,
    HttpContext httpContext,
    MemoryCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var key = BuildDocumentKey(tenantId, userId, path);
    var ifMatch = httpContext.Request.Headers.IfMatch.ToString();
    var actor = httpContext.Request.Headers["X-Service-Id"].ToString();
    if (string.IsNullOrWhiteSpace(actor))
    {
        actor = "unknown-service";
    }

    var result = await coordinator.ReplaceDocumentAsync(key, request, ifMatch, actor, cancellationToken);
    return Results.Ok(new
    {
        etag = result.ETag,
        document = result.Document
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

app.MapDelete("/v1/tenants/{tenantId}/users/{userId}/memory", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    DataLifecycleService lifecycleService,
    CancellationToken cancellationToken) =>
{
    var result = await lifecycleService.ForgetUserAsync(tenantId, userId, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/v1/tenants/{tenantId}/users/{userId}/retention:apply", async (
    [FromRoute] string tenantId,
    [FromRoute] string userId,
    [FromBody] ApplyRetentionRequest request,
    DataLifecycleService lifecycleService,
    CancellationToken cancellationToken) =>
{
    var result = await lifecycleService.ApplyRetentionAsync(tenantId, userId, request, cancellationToken);
    return Results.Ok(result);
});

app.Run();

static DocumentKey BuildDocumentKey(string tenantId, string userId, string path)
{
    var decodedPath = Uri.UnescapeDataString(path);
    return new DocumentKey(tenantId, userId, decodedPath);
}

public partial class Program;
