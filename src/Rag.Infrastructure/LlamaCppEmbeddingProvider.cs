using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class LlamaCppOptions
{
    public const string SectionName = "LlamaCpp";

    public string BaseUrl { get; set; } = "http://localhost:8080/";
}

public sealed class EmbeddingProviderException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class LlamaCppEmbeddingProvider(HttpClient httpClient, IOptions<EmbeddingOptions> options) : IEmbeddingProvider
{
    public async Task<EmbeddingResponse> EmbedAsync(
        EmbeddingProfile profile,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0 || inputs.Any(string.IsNullOrWhiteSpace))
        {
            throw new EmbeddingProviderException("Embedding input must contain at least one non-empty text value.");
        }

        var configuredProfiles = GetValidatedProfiles();
        if (profile != EmbeddingProfile.Default || !configuredProfiles.AllowedProfiles.Contains(profile))
        {
            throw new EmbeddingProviderException("The collection embedding profile is not allowed by the current configuration.");
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "v1/embeddings",
                new LlamaCppEmbeddingRequest(profile.Model, inputs),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<LlamaCppEmbeddingResponse>(cancellationToken: cancellationToken)
                ?? throw new EmbeddingProviderException("The embedding provider returned an empty response.");
            return new EmbeddingResponse(ValidateResponse(payload, profile, inputs.Count));
        }
        catch (EmbeddingProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new EmbeddingProviderException("The embedding provider response could not be used.", exception);
        }
    }

    private ValidatedEmbeddingOptions GetValidatedProfiles()
    {
        try
        {
            return options.Value.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new EmbeddingProviderException("The embedding profile configuration is invalid.", exception);
        }
    }

    private static IReadOnlyList<float[]> ValidateResponse(LlamaCppEmbeddingResponse payload, EmbeddingProfile profile, int expectedCount)
    {
        if (payload.Data is null || payload.Data.Count != expectedCount)
        {
            throw new EmbeddingProviderException("The embedding provider returned an unexpected embedding count.");
        }

        var vectors = new float[expectedCount][];
        for (var index = 0; index < payload.Data.Count; index++)
        {
            var item = payload.Data[index];
            if (item.Index != index)
            {
                throw new EmbeddingProviderException("The embedding provider returned embeddings in an invalid order.");
            }

            if (item.Embedding is null || item.Embedding.Length != profile.Dimensions || item.Embedding.Any(value => !float.IsFinite(value)))
            {
                throw new EmbeddingProviderException("The embedding provider returned invalid embedding values.");
            }

            vectors[index] = item.Embedding;
        }

        if (vectors.Any(vector => vector is null))
        {
            throw new EmbeddingProviderException("The embedding provider response omitted an embedding.");
        }

        return vectors!;
    }

    private sealed record LlamaCppEmbeddingRequest(
        string Model,
        IReadOnlyList<string> Input,
        [property: JsonPropertyName("encoding_format")] string EncodingFormat = "float");

    private sealed class LlamaCppEmbeddingResponse
    {
        public List<LlamaCppEmbeddingData>? Data { get; init; }
    }

    private sealed class LlamaCppEmbeddingData
    {
        public int Index { get; init; }

        public float[]? Embedding { get; init; }
    }
}
