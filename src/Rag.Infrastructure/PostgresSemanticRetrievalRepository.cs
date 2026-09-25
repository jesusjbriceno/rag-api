using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Application;

namespace Rag.Infrastructure;

public sealed class PostgresSemanticRetrievalRepository(IngestionDbContext dbContext) : ISemanticRetrievalRepository
{
    public async Task<IReadOnlyList<SemanticRetrievalMatch>> SearchAsync(
        IReadOnlyList<Guid> collectionIds,
        float[] queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collectionIds);
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        var queryVector = new Vector(queryEmbedding);
        return await (
            from embedding in dbContext.ChunkEmbeddings.AsNoTracking()
            join chunk in dbContext.Chunks.AsNoTracking() on embedding.ChunkId equals chunk.Id
            join documentVersion in dbContext.DocumentVersions.AsNoTracking() on chunk.DocumentVersionId equals documentVersion.Id
            join document in dbContext.Documents.AsNoTracking() on documentVersion.DocumentId equals document.Id
            where collectionIds.Contains(embedding.CollectionId)
                && document.CollectionId == embedding.CollectionId
                && document.CurrentVersionId == documentVersion.Id
            let distance = embedding.Values.CosineDistance(queryVector)
            orderby distance, chunk.Id
            select new SemanticRetrievalMatch(
                embedding.CollectionId,
                document.Id,
                documentVersion.Id,
                chunk.Id,
                chunk.Ordinal,
                chunk.Text,
                distance))
            .Take(topK)
            .ToListAsync(cancellationToken);
    }
}
