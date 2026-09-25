using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rag.Application;

namespace Rag.Api;

// Keys used to carry secret-free observability metadata from the auth handler and
// problem-details mapper to the observability middleware. Only allowlisted,
// non-secret values are ever written here; request/response bodies, headers,
// assertions, secrets, hashes, and salts are never read.
public static class AdminObservabilityKeys
{
    public const string AuthFailureReason = "admin.auth_failure_reason";
    public const string ProblemCode = "admin.problem_code";
}

public static class AdminProblem
{
    public static IResult Create(HttpContext context, int statusCode, string title, string code, int? retryAfterSeconds = null)
    {
        if (retryAfterSeconds is int seconds)
        {
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        context.Items[AdminObservabilityKeys.ProblemCode] = code;

        var details = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
        };
        details.Extensions["code"] = code;
        details.Extensions["traceId"] = context.TraceIdentifier;
        return Results.Problem(details);
    }
}

internal static class AdminEndpointSupport
{
    private const int MaxAdminBodyBytes = 1_048_576;

    public static IResult MapError(HttpContext context, Exception exception) => exception switch
    {
        AdminConflictException conflict => AdminProblem.Create(
            context,
            StatusCodes.Status409Conflict,
            "Conflict",
            conflict.Code,
            conflict.RetryAfterSeconds),
        ResourceNotFoundException => AdminProblem.Create(context, StatusCodes.Status404NotFound, "Not found", "not_found"),
        ArgumentException => AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input"),
        _ => AdminProblem.Create(context, StatusCodes.Status500InternalServerError, "Internal server error", "internal_error"),
    };

    public static bool TryParseIfMatch(string? ifMatch, out int version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return false;
        }

        var trimmed = ifMatch.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '"' || trimmed[^1] != '"')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        return inner.Length > 1 &&
            inner[0] == 'v' &&
            int.TryParse(inner[1..], NumberStyles.None, CultureInfo.InvariantCulture, out version) &&
            version >= 1;
    }

    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        if (!IsUtf8Json(context.Request.ContentType))
        {
            return (default, AdminProblem.Create(context, StatusCodes.Status415UnsupportedMediaType, "Unsupported content type", "unsupported_media_type"));
        }

        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[16_384];
            int read;
            while ((read = await context.Request.Body.ReadAsync(bytes, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxAdminBodyBytes)
                {
                    return (default, AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input"));
                }

                await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
            }

            var json = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return value is null
                ? (default, AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input"))
                : (value, null);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return (default, AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input"));
        }
    }

    private static bool IsUtf8Json(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var parts = contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(parts[0], "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Skip(1).All(part => !part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part[8..].Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));
    }
}
