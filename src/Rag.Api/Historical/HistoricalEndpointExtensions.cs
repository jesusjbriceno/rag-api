using Microsoft.Extensions.DependencyInjection;
using Rag.Application;
using Rag.Domain;

namespace Rag.Api.Historical;

public static class HistoricalAuthorizationPolicies
{
    public const string UploadsWrite = "HistoricalUploadsWrite";

    public const string OperationsRead = "HistoricalOperationsRead";
}

public static class HistoricalRateLimitPolicies
{
    public const string Uploads = "historical-uploads";
}

public static class HistoricalEndpointSupport
{
    public static string ToStateString(HistoricalUploadState state) => state switch
    {
        HistoricalUploadState.Reserved => "reserved",
        HistoricalUploadState.Published => "published",
        HistoricalUploadState.Committed => "committed",
        HistoricalUploadState.Abandoned => "abandoned",
        _ => state.ToString().ToLowerInvariant(),
    };

    public static bool IsUtf8Text(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var parts = contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Equals(parts[0], "text/plain", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryMap(Exception exception, HttpContext context, out IResult? result)
    {
        switch (exception)
        {
            case HistoricalUploadNotFoundException:
            case ResourceNotFoundException:
                result = Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
                return true;
            case HistoricalUploadConflictException:
                result = Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict");
                return true;
            case HistoricalQuotaExceededException:
            case HistoricalWatermarkExceededException:
                context.Response.Headers.RetryAfter = "30";
                result = Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests");
                return true;
            case HistoricalContentContractException:
            case ArgumentException:
                result = Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid input");
                return true;
            case HistoricalContentTooLargeException:
                result = Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request body too large");
                return true;
            default:
                result = null;
                return false;
        }
    }
}

public static class HistoricalEndpointExtensions
{
    public static IServiceCollection AddHistoricalIngestion(this IServiceCollection services)
    {
        services.AddScoped<HistoricalUploadHandler>();
        services.AddScoped<HistoricalOperationHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapHistoricalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<HistoricalIngestionOptions>();
        if (!options.Enabled)
        {
            return endpoints;
        }

        HistoricalUploadEndpoints.Map(endpoints);
        HistoricalOperationEndpoints.Map(endpoints);
        return endpoints;
    }
}
