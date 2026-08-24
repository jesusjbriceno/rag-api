using Rag.Domain;

namespace Rag.Application;

public sealed class EmbeddingProfileOptions
{
    public string Provider { get; set; } = EmbeddingProfile.Default.Provider;

    public string Model { get; set; } = EmbeddingProfile.Default.Model;

    public string Version { get; set; } = EmbeddingProfile.Default.Version;

    public int Dimensions { get; set; } = EmbeddingProfile.Default.Dimensions;

    public EmbeddingProfile ToProfile() => new(Provider, Model, Version, Dimensions);

    public static EmbeddingProfileOptions FromProfile(EmbeddingProfile profile) => new()
    {
        Provider = profile.Provider,
        Model = profile.Model,
        Version = profile.Version,
        Dimensions = profile.Dimensions,
    };
}

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    public EmbeddingProfileOptions Default { get; set; } = EmbeddingProfileOptions.FromProfile(EmbeddingProfile.Default);

    public List<EmbeddingProfileOptions> AllowedProfiles { get; set; } = [];

    public ValidatedEmbeddingOptions Validate()
    {
        var defaultProfile = Default.ToProfile();
        var allowedProfiles = AllowedProfiles.Select(profile => profile.ToProfile()).ToArray();
        if (allowedProfiles.Length == 0)
        {
            throw new InvalidOperationException("Embeddings:AllowedProfiles must contain at least one profile.");
        }

        if (allowedProfiles.Any(profile => !string.Equals(profile.Provider, "ollama", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Only the ollama embedding provider is currently supported.");
        }

        if (allowedProfiles.Distinct().Count() != allowedProfiles.Length)
        {
            throw new InvalidOperationException("Embeddings:AllowedProfiles cannot contain duplicates.");
        }

        if (!allowedProfiles.Contains(defaultProfile))
        {
            throw new InvalidOperationException("Embeddings:Default must be included in Embeddings:AllowedProfiles.");
        }

        return new ValidatedEmbeddingOptions(defaultProfile, allowedProfiles);
    }
}

public sealed record ValidatedEmbeddingOptions(EmbeddingProfile DefaultProfile, IReadOnlyList<EmbeddingProfile> AllowedProfiles);

public sealed record EmbeddingResponse(IReadOnlyList<float[]> Vectors);

public interface IEmbeddingProvider
{
    Task<EmbeddingResponse> EmbedAsync(EmbeddingProfile profile, IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}

public interface ICollectionEmbeddingProfileRepository
{
    Task<EmbeddingProfile?> GetProfileAsync(Guid serviceClientId, Guid collectionId, CancellationToken cancellationToken);
}

public sealed class QueryEmbeddingService(
    ICollectionEmbeddingProfileRepository collections,
    IEmbeddingProvider embeddingProvider)
{
    public async Task<float[]> EmbedAsync(Guid serviceClientId, Guid collectionId, string query, CancellationToken cancellationToken = default)
    {
        if (serviceClientId == Guid.Empty || collectionId == Guid.Empty)
        {
            throw new ArgumentException("A collection id is required.", nameof(collectionId));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A query is required.", nameof(query));
        }

        var profile = await collections.GetProfileAsync(serviceClientId, collectionId, cancellationToken)
            ?? throw new ResourceNotFoundException();
        var response = await embeddingProvider.EmbedAsync(profile, [query], cancellationToken);
        if (response.Vectors.Count != 1)
        {
            throw new InvalidOperationException("The embedding provider returned an unexpected query embedding count.");
        }

        var vector = response.Vectors[0];
        if (vector.Length != profile.Dimensions || vector.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidOperationException("The embedding provider returned a query vector incompatible with the collection profile.");
        }

        return vector;
    }
}
