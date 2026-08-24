namespace Rag.Domain;

public sealed class Collection
{
    private Collection()
    {
    }

    public Collection(Guid id, Guid serviceClientId, string name, DateTimeOffset createdAt, EmbeddingProfile embeddingProfile)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A collection id is required.", nameof(id));
        }

        if (serviceClientId == Guid.Empty)
        {
            throw new ArgumentException("A service client id is required.", nameof(serviceClientId));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new ArgumentException("A collection name is required and must not exceed 200 characters.", nameof(name));
        }

        Id = id;
        ServiceClientId = serviceClientId;
        Name = name.Trim();
        CreatedAt = createdAt;
        EmbeddingProvider = embeddingProfile.Provider;
        EmbeddingModel = embeddingProfile.Model;
        EmbeddingVersion = embeddingProfile.Version;
        EmbeddingDimensions = embeddingProfile.Dimensions;
    }

    public Guid Id { get; private set; }

    public Guid ServiceClientId { get; private set; }

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
