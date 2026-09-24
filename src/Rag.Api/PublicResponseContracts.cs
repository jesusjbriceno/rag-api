using System.Text.Json.Serialization;

namespace Rag.Api;

// These records mirror the JSON published by the public plane. Members whose wire name is snake_case carry an
// explicit [JsonPropertyName] on purpose, so the published contract stays stable and can be declared later.
public sealed record HealthResponse(string Status, string Version);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("scope")]
    string? Scope);

public sealed record TxtIngestionResponse(
    [property: JsonPropertyName("document_id")] Guid DocumentId,
    [property: JsonPropertyName("document_version_id")] Guid DocumentVersionId,
    [property: JsonPropertyName("operation_id")] Guid? OperationId);

public sealed record OperationStatusResponse(
    Guid Id,
    string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("failure_stage")] string? FailureStage);
