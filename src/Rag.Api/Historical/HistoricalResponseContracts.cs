using System.Text.Json.Serialization;

namespace Rag.Api.Historical;

// These records mirror the JSON published by the historical plane. Members whose wire name is snake_case carry
// an explicit [JsonPropertyName] on purpose, so the published contract stays stable and can be declared later.
public sealed record ReserveHistoricalUploadResponse(
    [property: JsonPropertyName("upload_id")] Guid UploadId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("created")] bool Created,
    [property: JsonPropertyName("accepted_limits")] HistoricalUploadLimits AcceptedLimits);

public sealed record HistoricalUploadLimits(
    [property: JsonPropertyName("max_normalized_text_bytes")] int MaxNormalizedTextBytes,
    [property: JsonPropertyName("per_client_pending_quota")] int PerClientPendingQuota,
    [property: JsonPropertyName("total_storage_watermark_bytes")] long TotalStorageWatermarkBytes,
    [property: JsonPropertyName("abandoned_upload_expiry")] TimeSpan AbandonedUploadExpiry);

public sealed record PublishedHistoricalUploadResponse(
    [property: JsonPropertyName("upload_id")] Guid UploadId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("declared_bytes")] long DeclaredBytes,
    [property: JsonPropertyName("observed_bytes")] long ObservedBytes,
    [property: JsonPropertyName("normalized_text_sha256")] string NormalizedTextSha256);

public sealed record CommitHistoricalUploadResponse(
    [property: JsonPropertyName("upload_id")] Guid UploadId,
    [property: JsonPropertyName("document_id")] Guid DocumentId,
    [property: JsonPropertyName("document_version_id")] Guid DocumentVersionId,
    [property: JsonPropertyName("operation_id")] Guid OperationId,
    [property: JsonPropertyName("state")] string State);

public sealed record HistoricalUploadStatusResponse(
    [property: JsonPropertyName("upload_id")] Guid UploadId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("source_document_key")] string SourceDocumentKey,
    [property: JsonPropertyName("normalized_text_sha256")] string NormalizedTextSha256,
    [property: JsonPropertyName("declared_bytes")] long DeclaredBytes,
    [property: JsonPropertyName("document_id")] Guid? DocumentId,
    [property: JsonPropertyName("document_version_id")] Guid? DocumentVersionId,
    [property: JsonPropertyName("operation_id")] Guid? OperationId,
    [property: JsonPropertyName("correlation_id")] string CorrelationId);

public sealed record HistoricalOperationTelemetryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failure_stage")] string? FailureStage,
    [property: JsonPropertyName("failure_code")] string? FailureCode,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("queue_wait")] TimeSpan QueueWait,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("chunking_duration")] TimeSpan ChunkingDuration,
    [property: JsonPropertyName("embedding_calls")] int EmbeddingCalls,
    [property: JsonPropertyName("embedding_duration")] TimeSpan EmbeddingDuration,
    [property: JsonPropertyName("indexing_duration")] TimeSpan IndexingDuration,
    [property: JsonPropertyName("terminal_state")] string? TerminalState);
