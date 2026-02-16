using System.Net;

namespace MemNet.Client;

public class MemNetException : Exception
{
    public MemNetException(string message)
        : base(message)
    {
    }

    public MemNetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MemNetApiException : MemNetException
{
    public MemNetApiException(MemNetApiError error)
        : base($"mem.net API error {(int)error.StatusCode} {error.StatusCode}: {error.Code} - {error.Message}")
    {
        StatusCode = error.StatusCode;
        Code = error.Code;
        RequestId = error.RequestId;
        Details = error.Details;
        RawResponseBody = error.RawResponseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string Code { get; }

    public string? RequestId { get; }

    public IReadOnlyDictionary<string, string>? Details { get; }

    public string? RawResponseBody { get; }
}

public sealed class MemNetTransportException : MemNetException
{
    public MemNetTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
