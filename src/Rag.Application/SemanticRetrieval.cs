using Rag.Domain;

namespace Rag.Application;

public sealed record SemanticRetrievalQuery(
    IReadOnlyList<Guid> CollectionIds,
    string Query,
    int TopK);

public sealed record SemanticRetrievalMatch(
    Guid CollectionId,
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid ChunkId,
    int ChunkOrdinal,
    string ChunkText,
    double CosineDistance);

public interface ISemanticRetrievalRepository
{
    Task<IReadOnlyList<SemanticRetrievalMatch>> SearchAsync(
        IReadOnlyList<Guid> collectionIds,
        float[] queryEmbedding,
        int topK,
        CancellationToken cancellationToken);
}

public sealed class SemanticRetrievalHandler(
    ICollectionEmbeddingProfileRepository collectionProfiles,
    IEmbeddingProvider embeddingProvider,
    ISemanticRetrievalRepository repository)
{
    public async Task<IReadOnlyList<SemanticRetrievalMatch>> HandleAsync(
        SemanticRetrievalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.CollectionIds);
        if (query.CollectionIds.Count == 0 || query.CollectionIds.Any(collectionId => collectionId == Guid.Empty))
        {
            throw new ArgumentException("At least one collection id is required.", nameof(query));
        }

        if (query.CollectionIds.Distinct().Count() != query.CollectionIds.Count)
        {
            throw new ArgumentException("Collection ids must be distinct.", nameof(query));
        }

        if (string.IsNullOrWhiteSpace(query.Query))
        {
            throw new ArgumentException("A query is required.", nameof(query));
        }

        if (query.TopK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Top-k must be positive.");
        }

        var profiles = new List<(Guid CollectionId, EmbeddingProfile? Profile)>(query.CollectionIds.Count);
        foreach (var collectionId in query.CollectionIds)
        {
            profiles.Add((collectionId, await collectionProfiles.GetProfileAsync(collectionId, cancellationToken)));
        }
        if (profiles.Any(item => item.Profile is null))
        {
            throw new InvalidOperationException("One or more collections do not exist.");
        }

        var sharedProfile = profiles[0].Profile!;
        if (profiles.Any(item => item.Profile != sharedProfile))
        {
            throw new InvalidOperationException("Requested collections have incompatible embedding profiles.");
        }

        var response = await embeddingProvider.EmbedAsync(sharedProfile, [query.Query], cancellationToken);
        if (response.Vectors.Count != 1)
        {
            throw new InvalidOperationException("The embedding provider returned an unexpected query embedding count.");
        }

        var vector = response.Vectors[0];
        if (vector.Length != sharedProfile.Dimensions || vector.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidOperationException("The embedding provider returned a query vector incompatible with the collection profile.");
        }

        return await repository.SearchAsync(query.CollectionIds, vector, query.TopK, cancellationToken);
    }
}
