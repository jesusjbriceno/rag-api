using System.Text.Json.Serialization;
using Rag.Domain;

namespace Rag.Api.Historical;

public static class HistoricalUploadEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/v1/historical/collections/{collectionId:guid}/uploads",
            ReserveAsync)
            .WithName("reserve_historical_upload")
            .DeclaresBody<ReserveHistoricalUploadRequest>("application/json", endpoints.IsOpenApiGeneration())
            .Produces<ReserveHistoricalUploadResponse>(StatusCodes.Status201Created)
            .Produces<ReserveHistoricalUploadResponse>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The JSON body is malformed or empty, or the reserve command is invalid; the handler " +
                "answers application/problem+json titled \"Invalid input\".")
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The collection does not exist or does not belong to the caller; the handler answers " +
                "application/problem+json titled \"Not found\".")
            .DeclaresProblem(
                StatusCodes.Status409Conflict,
                "Conflict. The idempotency key was reused with a different reserve fingerprint; the handler answers " +
                "application/problem+json titled \"Conflict\".")
            .DeclaresProblem(
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large. The reserve body exceeds 16,384 bytes, whether declared through Content-Length " +
                "or observed while streaming; the handler answers application/problem+json titled \"Request body too large\".")
            .DeclaresProblem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported content type. The handler only accepts application/json and answers application/problem+json " +
                "titled \"Unsupported content type\".")
            .DeclaresProblem(
                StatusCodes.Status429TooManyRequests,
                "Too many requests. The per-client pending-upload quota or the total storage watermark was exceeded; " +
                "the handler answers application/problem+json with Retry-After: 30. The route is also rate limited, and " +
                "the limiter answers 429 with no response body.",
                retryAfterDescription: "Seconds to wait before retrying; the quota and watermark paths set " +
                    "Retry-After: 30 on the problem response.")
            .RequireAuthorization(HistoricalAuthorizationPolicies.UploadsWrite)
            .RequireRateLimiting(HistoricalRateLimitPolicies.Uploads);

        endpoints.MapPut(
            "/api/v1/historical/uploads/{uploadId:guid}/content",
            PublishContentAsync)
            .WithName("publish_historical_upload_content")
            .DeclaresBody<string>("text/plain", endpoints.IsOpenApiGeneration())
            .Produces<PublishedHistoricalUploadResponse>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The observed content length or SHA-256 does not match the reserve, or the content " +
                "contract is invalid; the handler answers application/problem+json titled \"Invalid input\".")
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The upload does not exist or does not belong to the caller; the handler answers " +
                "application/problem+json titled \"Not found\".")
            .DeclaresProblem(
                StatusCodes.Status409Conflict,
                "Conflict. The upload is already committed or was abandoned; the handler answers " +
                "application/problem+json titled \"Conflict\".")
            .DeclaresProblem(
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large. The content exceeds the configured maximum normalized text size; the handler " +
                "answers application/problem+json titled \"Request body too large\".")
            .DeclaresProblem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported content type. The content body must be text/plain; the handler answers " +
                "application/problem+json titled \"Unsupported content type\".")
            .DeclaresProblem(
                StatusCodes.Status429TooManyRequests,
                "Too many requests. Publishing would cross the total storage watermark; the handler answers " +
                "application/problem+json with Retry-After: 30.",
                retryAfterDescription: "Seconds to wait before retrying; the watermark path sets it to 30 on the problem response.")
            .RequireAuthorization(HistoricalAuthorizationPolicies.UploadsWrite);

        endpoints.MapPost(
            "/api/v1/historical/uploads/{uploadId:guid}:commit",
            CommitAsync)
            .WithName("commit_historical_upload")
            .Produces<CommitHistoricalUploadResponse>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The upload or its collection does not exist or does not belong to the caller; the handler " +
                "answers application/problem+json titled \"Not found\".")
            .DeclaresProblem(
                StatusCodes.Status409Conflict,
                "Conflict. The upload is not in the published state; the handler answers application/problem+json " +
                "titled \"Conflict\".")
            .RequireAuthorization(HistoricalAuthorizationPolicies.UploadsWrite);

        endpoints.MapGet(
            "/api/v1/historical/uploads/{uploadId:guid}",
            GetAsync)
            .WithName("get_historical_upload")
            .Produces<HistoricalUploadStatusResponse>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The upload does not exist or does not belong to the caller; the handler answers " +
                "application/problem+json titled \"Not found\".")
            .RequireAuthorization(HistoricalAuthorizationPolicies.UploadsWrite);
    }

    private static async Task<IResult> ReserveAsync(
        Guid collectionId,
        HttpContext context,
        HistoricalUploadHandler handler,
        CancellationToken cancellationToken)
    {
        var payload = await ApiEndpointSupport.ReadJsonAsync<ReserveHistoricalUploadRequest>(context.Request, 16_384, cancellationToken);
        if (payload.Error is not null)
        {
            return payload.Error;
        }

        try
        {
            var value = payload.Value!;
            var result = await handler.ReserveAsync(
                new ReserveHistoricalUploadCommand(
                    ApiEndpointSupport.GetClientId(context.User),
                    collectionId,
                    value.SourceDocumentKey ?? string.Empty,
                    value.IdempotencyKey ?? string.Empty,
                    value.NormalizedTextSha256 ?? string.Empty,
                    value.DeclaredBytes,
                    value.CandidateId,
                    value.ManifestId,
                    value.RunId,
                    value.DisplayName,
                    value.Format,
                    value.SourceRootAlias),
                cancellationToken);
            return Results.Json(new ReserveHistoricalUploadResponse(
                result.UploadId,
                HistoricalEndpointSupport.ToStateString(result.State),
                result.CorrelationId,
                result.Created,
                new HistoricalUploadLimits(
                    result.MaxNormalizedTextBytes,
                    result.PerClientPendingQuota,
                    result.TotalStorageWatermarkBytes,
                    result.AbandonedUploadExpiry)),
                statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }
        catch (Exception exception) when (HistoricalEndpointSupport.TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> PublishContentAsync(
        Guid uploadId,
        HttpContext context,
        HistoricalUploadHandler handler,
        CancellationToken cancellationToken)
    {
        if (!HistoricalEndpointSupport.IsUtf8Text(context.Request.ContentType))
        {
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported content type");
        }

        try
        {
            var result = await handler.PublishContentAsync(
                uploadId,
                ApiEndpointSupport.GetClientId(context.User),
                context.Request.Body,
                cancellationToken);
            return Results.Ok(new PublishedHistoricalUploadResponse(
                result.UploadId,
                HistoricalEndpointSupport.ToStateString(result.State),
                result.DeclaredBytes,
                result.ObservedBytes,
                result.NormalizedTextSha256));
        }
        catch (Exception exception) when (HistoricalEndpointSupport.TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> CommitAsync(
        Guid uploadId,
        HttpContext context,
        HistoricalUploadHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.CommitAsync(
                uploadId,
                ApiEndpointSupport.GetClientId(context.User),
                cancellationToken);
            return Results.Ok(new CommitHistoricalUploadResponse(
                result.UploadId,
                result.DocumentId,
                result.DocumentVersionId,
                result.OperationId,
                HistoricalEndpointSupport.ToStateString(result.State)));
        }
        catch (Exception exception) when (HistoricalEndpointSupport.TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> GetAsync(
        Guid uploadId,
        HttpContext context,
        HistoricalUploadHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.GetAsync(
                uploadId,
                ApiEndpointSupport.GetClientId(context.User),
                cancellationToken);
            return Results.Ok(new HistoricalUploadStatusResponse(
                result.UploadId,
                HistoricalEndpointSupport.ToStateString(result.State),
                result.SourceDocumentKey,
                result.NormalizedTextSha256,
                result.DeclaredBytes,
                result.DocumentId,
                result.DocumentVersionId,
                result.OperationId,
                result.CorrelationId));
        }
        catch (Exception exception) when (HistoricalEndpointSupport.TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }
}

public sealed record ReserveHistoricalUploadRequest(
    [property: JsonPropertyName("source_document_key")] string? SourceDocumentKey,
    [property: JsonPropertyName("candidate_id")] Guid? CandidateId,
    [property: JsonPropertyName("manifest_id")] Guid? ManifestId,
    [property: JsonPropertyName("run_id")] Guid? RunId,
    [property: JsonPropertyName("normalized_text_sha256")] string? NormalizedTextSha256,
    [property: JsonPropertyName("declared_bytes")] long DeclaredBytes,
    [property: JsonPropertyName("idempotency_key")] string? IdempotencyKey,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("source_root_alias")] string? SourceRootAlias);
