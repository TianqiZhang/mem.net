using System.Net;
using System.Text;
using System.Text.Json;

namespace MemNet.Client;

public sealed class MemNetClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly MemNetClientOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly bool _ownsHttpClient;

    public MemNetClient(MemNetClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = options.JsonSerializerOptions;

        if (options.HttpClient is not null)
        {
            _httpClient = options.HttpClient;
            _ownsHttpClient = false;
        }
        else
        {
            if (options.BaseAddress is null)
            {
                throw new ArgumentException("MemNetClientOptions.BaseAddress must be set when HttpClient is not provided.", nameof(options));
            }

            _httpClient = new HttpClient
            {
                BaseAddress = options.BaseAddress
            };
            _ownsHttpClient = true;
        }

        if (_httpClient.BaseAddress is null && options.BaseAddress is not null)
        {
            _httpClient.BaseAddress = options.BaseAddress;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<ServiceStatusResponse> GetServiceStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "/"),
            allowRetry: true,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<ServiceStatusResponse>(response, cancellationToken);
    }

    public async Task<DocumentReadResult> GetDocumentAsync(MemNetScope scope, DocumentRef document, CancellationToken cancellationToken = default)
    {
        var route = BuildDocumentRoute(scope, document);
        using var response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(HttpMethod.Get, route),
            allowRetry: true,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<DocumentReadResult>(response, cancellationToken);
    }

    public async Task<DocumentMutationResult> PatchDocumentAsync(
        MemNetScope scope,
        DocumentRef document,
        PatchDocumentRequest request,
        string ifMatch,
        string? serviceId = null,
        CancellationToken cancellationToken = default)
    {
        var route = BuildDocumentRoute(scope, document);
        using var response = await SendWithRetriesAsync(
            () =>
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Patch, route)
                {
                    Content = CreateJsonContent(request)
                };
                httpRequest.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                AddServiceIdHeader(httpRequest, serviceId);
                return httpRequest;
            },
            allowRetry: false,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<DocumentMutationResult>(response, cancellationToken);
    }

    public async Task<DocumentMutationResult> ReplaceDocumentAsync(
        MemNetScope scope,
        DocumentRef document,
        ReplaceDocumentRequest request,
        string ifMatch,
        string? serviceId = null,
        CancellationToken cancellationToken = default)
    {
        var route = BuildDocumentRoute(scope, document);
        using var response = await SendWithRetriesAsync(
            () =>
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Put, route)
                {
                    Content = CreateJsonContent(request)
                };
                httpRequest.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                AddServiceIdHeader(httpRequest, serviceId);
                return httpRequest;
            },
            allowRetry: false,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<DocumentMutationResult>(response, cancellationToken);
    }

    public async Task<AssembleContextResponse> AssembleContextAsync(MemNetScope scope, AssembleContextRequest request, CancellationToken cancellationToken = default)
    {
        var route = BuildScopeRoute(scope, "/context:assemble");
        using var response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(HttpMethod.Post, route) { Content = CreateJsonContent(request) },
            allowRetry: true,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<AssembleContextResponse>(response, cancellationToken);
    }

    public async Task WriteEventAsync(MemNetScope scope, WriteEventRequest request, CancellationToken cancellationToken = default)
    {
        var route = BuildScopeRoute(scope, "/events");
        using var response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(HttpMethod.Post, route) { Content = CreateJsonContent(request) },
            allowRetry: false,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<SearchEventsResponse> SearchEventsAsync(MemNetScope scope, SearchEventsRequest request, CancellationToken cancellationToken = default)
    {
        var route = BuildScopeRoute(scope, "/events:search");
        using var response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(HttpMethod.Post, route) { Content = CreateJsonContent(request) },
            allowRetry: true,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<SearchEventsResponse>(response, cancellationToken);
    }

    public async Task<RetentionSweepResult> ApplyRetentionAsync(MemNetScope scope, ApplyRetentionRequest request, CancellationToken cancellationToken = default)
    {
        var route = BuildScopeRoute(scope, "/retention:apply");
        using var response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(HttpMethod.Post, route) { Content = CreateJsonContent(request) },
            allowRetry: false,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<RetentionSweepResult>(response, cancellationToken);
    }

    public async Task<ForgetUserResult> ForgetUserAsync(MemNetScope scope, CancellationToken cancellationToken = default)
    {
        var route = BuildScopeRoute(scope, "/memory");
        using var response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, route),
            allowRetry: false,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<ForgetUserResult>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<HttpRequestMessage> requestFactory,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        var maxRetries = Math.Max(0, _options.Retry.MaxRetries);

        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            await ApplyCustomHeadersAsync(request, cancellationToken);
            _options.OnRequest?.Invoke(request);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                if (allowRetry && attempt < maxRetries)
                {
                    await DelayForRetryAsync(attempt, cancellationToken);
                    continue;
                }

                throw new MemNetTransportException("HTTP transport failure while calling mem.net.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (allowRetry && attempt < maxRetries)
                {
                    await DelayForRetryAsync(attempt, cancellationToken);
                    continue;
                }

                throw new MemNetTransportException("HTTP timeout while calling mem.net.", ex);
            }

            _options.OnResponse?.Invoke(response);

            if (allowRetry && attempt < maxRetries && ShouldRetryStatus(response.StatusCode))
            {
                response.Dispose();
                await DelayForRetryAsync(attempt, cancellationToken);
                continue;
            }

            return response;
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiErrorEnvelope? envelope = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                envelope = JsonSerializer.Deserialize<ApiErrorEnvelope>(rawBody, _jsonOptions);
            }
        }
        catch (JsonException)
        {
            // fall through to fallback mapping below
        }

        var trimmedBody = rawBody.Length > 1024 ? rawBody[..1024] : rawBody;
        if (envelope?.Error is { } apiError)
        {
            throw new MemNetApiException(new MemNetApiError(
                StatusCode: response.StatusCode,
                Code: apiError.Code,
                Message: apiError.Message,
                RequestId: apiError.RequestId,
                Details: apiError.Details,
                RawResponseBody: trimmedBody));
        }

        throw new MemNetApiException(new MemNetApiError(
            StatusCode: response.StatusCode,
            Code: $"HTTP_{(int)response.StatusCode}",
            Message: string.IsNullOrWhiteSpace(trimmedBody) ? response.ReasonPhrase ?? "Unexpected error" : trimmedBody,
            RequestId: null,
            Details: null,
            RawResponseBody: trimmedBody));
    }

    private async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new MemNetException($"Expected JSON payload for {typeof(T).Name} but response body was empty.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, _jsonOptions)
                ?? throw new MemNetException($"Failed to deserialize {typeof(T).Name} from mem.net response.");
        }
        catch (JsonException ex)
        {
            throw new MemNetException($"Invalid JSON payload returned by mem.net for {typeof(T).Name}.", ex);
        }
    }

    private StringContent CreateJsonContent<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private async Task ApplyCustomHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.HeaderProvider is null)
        {
            return;
        }

        var headers = await _options.HeaderProvider(cancellationToken);
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!request.Headers.Contains(key))
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    private void AddServiceIdHeader(HttpRequestMessage request, string? serviceIdOverride)
    {
        var serviceId = string.IsNullOrWhiteSpace(serviceIdOverride)
            ? _options.ServiceId
            : serviceIdOverride;

        if (!string.IsNullOrWhiteSpace(serviceId) && !request.Headers.Contains("X-Service-Id"))
        {
            request.Headers.TryAddWithoutValidation("X-Service-Id", serviceId);
        }
    }

    private static string BuildDocumentRoute(MemNetScope scope, DocumentRef document)
    {
        var encodedNamespace = EncodeSegment(document.Namespace);
        var encodedPath = EncodePath(document.Path);
        return BuildScopeRoute(scope, $"/documents/{encodedNamespace}/{encodedPath}");
    }

    private static string BuildScopeRoute(MemNetScope scope, string suffix)
    {
        var tenantId = EncodeSegment(scope.TenantId);
        var userId = EncodeSegment(scope.UserId);
        return $"/v1/tenants/{tenantId}/users/{userId}{suffix}";
    }

    private static string EncodeSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static string EncodePath(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return string.Join('/', trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private async Task DelayForRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponent = Math.Min(attempt, 8);
        var scale = 1 << exponent;
        var baseDelayMs = _options.Retry.BaseDelay.TotalMilliseconds * scale;
        var jitterMs = Random.Shared.Next(0, 100);
        var delayMs = Math.Min(baseDelayMs + jitterMs, _options.Retry.MaxDelay.TotalMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
    }

    private static bool ShouldRetryStatus(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests
            || statusCode == HttpStatusCode.ServiceUnavailable
            || statusCode == HttpStatusCode.BadGateway
            || statusCode == HttpStatusCode.GatewayTimeout;
    }
}
