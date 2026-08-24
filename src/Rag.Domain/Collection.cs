namespace Rag.Domain;

public sealed class Collection
{
    private Collection()
    {
    }

    public Collection(Guid id, string name, DateTimeOffset createdAt, EmbeddingProfile? embeddingProfile = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A collection id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A collection name is required.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        CreatedAt = createdAt;
        var profile = embeddingProfile ?? EmbeddingProfile.Default;
        EmbeddingProvider = profile.Provider;
        EmbeddingModel = profile.Model;
        EmbeddingVersion = profile.Version;
        EmbeddingDimensions = profile.Dimensions;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public string EmbeddingProvider { get; private set; } = null!;

    public string EmbeddingModel { get; private set; } = null!;

    public string EmbeddingVersion { get; private set; } = null!;

    public int EmbeddingDimensions { get; private set; }

    public EmbeddingProfile GetEmbeddingProfile() => new(
        EmbeddingProvider,
        EmbeddingModel,
        EmbeddingVersion,
        EmbeddingDimensions);
}
