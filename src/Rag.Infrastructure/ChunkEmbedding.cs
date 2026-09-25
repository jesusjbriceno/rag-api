using Pgvector;

namespace Rag.Infrastructure;

public sealed class ChunkEmbedding
{
    private ChunkEmbedding()
    {
    }

    public ChunkEmbedding(Guid id, Guid collectionId, Guid chunkId, float[] values)
    {
        if (id == Guid.Empty || collectionId == Guid.Empty || chunkId == Guid.Empty)
        {
            throw new ArgumentException("Embedding, collection, and chunk ids are required.");
        }

        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0 || values.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("Embedding values must be finite and non-empty.", nameof(values));
        }

        Id = id;
        CollectionId = collectionId;
        ChunkId = chunkId;
        Values = new Vector(values);
    }

    public Guid Id { get; private set; }

    public Guid CollectionId { get; private set; }

    public Guid ChunkId { get; private set; }

    public Vector Values { get; private set; } = null!;
}
