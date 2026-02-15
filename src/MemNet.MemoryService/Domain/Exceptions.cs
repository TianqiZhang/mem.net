using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Core;

public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message, IReadOnlyDictionary<string, string>? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
    }

    public int StatusCode { get; }

    public string Code { get; }

    public IReadOnlyDictionary<string, string>? Details { get; }
}

public static class Guard
{
    public static void NotNull<T>(T? value, string code, string message, int statusCode = StatusCodes.Status400BadRequest)
    {
        if (value is null)
        {
            throw new ApiException(statusCode, code, message);
        }
    }

    public static void True(bool condition, string code, string message, int statusCode = StatusCodes.Status400BadRequest)
    {
        if (!condition)
        {
            throw new ApiException(statusCode, code, message);
        }
    }
}
