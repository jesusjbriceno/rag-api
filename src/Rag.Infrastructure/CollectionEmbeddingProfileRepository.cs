using Microsoft.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class CollectionEmbeddingProfileRepository(IngestionDbContext dbContext) : ICollectionEmbeddingProfileRepository
{
    public async Task<EmbeddingProfile?> GetProfileAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await dbContext.Collections.AsNoTracking()
            .Where(item => item.Id == collectionId)
            .Select(item => new
            {
                item.EmbeddingProvider,
                item.EmbeddingModel,
                item.EmbeddingVersion,
                item.EmbeddingDimensions,
            })
            .SingleOrDefaultAsync(cancellationToken);
        return collection is null
            ? null
            : new EmbeddingProfile(
                collection.EmbeddingProvider,
                collection.EmbeddingModel,
                collection.EmbeddingVersion,
                collection.EmbeddingDimensions);
    }
}
