using System.Text.Json.Serialization;

namespace Rag.Companion.Adapters;

/// <summary>
/// Body of the TXT ingestion request. Content is normalized UTF-8 text; <see cref="ExternalReference"/>
/// is the SHA-256 of that text. Field names match the data-plane API contract (<c>file_name</c>,
/// <c>content</c>, <c>external_reference</c>).
/// </summary>
public sealed record TxtIngestionPayload
{
    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    public required string Content { get; init; }

    [JsonPropertyName("external_reference")]
    public required string ExternalReference { get; init; }
}
