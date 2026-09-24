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
            .RequireAuthorization(HistoricalAuthorizationPolicies.UploadsWrite)
            .RequireRateLimiting(HistoricalRateLimitPolicies.Uploads);

        endpoints.MapPut(
            "/api/v1/historical/uploads/{uploadId:guid}/content",
            PublishContentAsync)
            .RequireAuthorization(HistoricalAuthorizationPolicies.UploadsWrite);

        endpoints.MapPost(
            "/api/v1/historical/uploads/{uploadId:guid}:commit",
            CommitAsync)
            .RequireAuthorization(HistoricalAuthorizationPolicies.UploadsWrite);

        endpoints.MapGet(
            "/api/v1/historical/uploads/{uploadId:guid}",
            GetAsync)
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
