using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgresSemanticRetrievalRepositoryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Retrieves_only_current_version_chunks_in_cosine_and_chunk_id_order_with_provenance()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var profile = new EmbeddingProfile("ollama", "test:1", "1", 3);
        var requestedCollectionId = new Guid("00000000-0000-0000-0000-000000000010");
        var otherCollectionId = new Guid("00000000-0000-0000-0000-000000000020");
        var documentId = new Guid("00000000-0000-0000-0000-000000000100");
        var historicalVersionId = new Guid("00000000-0000-0000-0000-000000000101");
        var currentVersionId = new Guid("00000000-0000-0000-0000-000000000102");
        var firstCurrentChunkId = new Guid("00000000-0000-0000-0000-000000000201");
        var secondCurrentChunkId = new Guid("00000000-0000-0000-0000-000000000202");
        var distantCurrentChunkId = new Guid("00000000-0000-0000-0000-000000000203");

        await using (var context = new IngestionDbContext(options))
        {
            var now = DateTimeOffset.UtcNow;
            var requestedCollection = new Collection(requestedCollectionId, "Requested collection", now, profile);
            var otherCollection = new Collection(otherCollectionId, "Other collection", now, profile);
            var document = new Document(documentId, requestedCollection.Id, "source://requested", now);
            var historicalVersion = document.AddVersion(
                historicalVersionId,
                "historical.txt",
                ContentHash.FromBytes("historical"u8),
                ContentReference.ForVersion(historicalVersionId),
                now);
            var currentVersion = document.AddVersion(
                currentVersionId,
                "current.txt",
                ContentHash.FromBytes("current"u8),
                ContentReference.ForVersion(currentVersionId),
                now);
            var otherDocument = new Document(Guid.NewGuid(), otherCollection.Id, "source://other", now);
            var otherVersionId = Guid.NewGuid();
            var otherVersion = otherDocument.AddVersion(
                otherVersionId,
                "other.txt",
                ContentHash.FromBytes("other"u8),
                ContentReference.ForVersion(otherVersionId),
                now);
            var historicalChunk = new Chunk(Guid.NewGuid(), historicalVersion.Id, 1, "historical text");
            var firstCurrentChunk = new Chunk(firstCurrentChunkId, currentVersion.Id, 1, "first current text");
            var secondCurrentChunk = new Chunk(secondCurrentChunkId, currentVersion.Id, 2, "second current text");
            var distantCurrentChunk = new Chunk(distantCurrentChunkId, currentVersion.Id, 3, "distant current text");
            var otherChunk = new Chunk(Guid.NewGuid(), otherVersion.Id, 1, "other collection text");
            context.Collections.AddRange(requestedCollection, otherCollection);
            context.Documents.AddRange(document, otherDocument);
            context.Chunks.AddRange(historicalChunk, firstCurrentChunk, secondCurrentChunk, distantCurrentChunk, otherChunk);
            context.ChunkEmbeddings.AddRange(
                new ChunkEmbedding(Guid.NewGuid(), requestedCollection.Id, historicalChunk.Id, [1, 0, 0]),
                new ChunkEmbedding(Guid.NewGuid(), requestedCollection.Id, firstCurrentChunk.Id, [1, 0, 0]),
                new ChunkEmbedding(Guid.NewGuid(), requestedCollection.Id, secondCurrentChunk.Id, [1, 0, 0]),
                new ChunkEmbedding(Guid.NewGuid(), requestedCollection.Id, distantCurrentChunk.Id, [0, 1, 0]),
                new ChunkEmbedding(Guid.NewGuid(), otherCollection.Id, otherChunk.Id, [1, 0, 0]));
            await context.SaveChangesAsync();
        }

        await using var searchContext = new IngestionDbContext(options);
        var results = await new PostgresSemanticRetrievalRepository(searchContext).SearchAsync(
            [requestedCollectionId],
            [1, 0, 0],
            5,
            CancellationToken.None);

        Assert.Equal([firstCurrentChunkId, secondCurrentChunkId, distantCurrentChunkId], results.Select(result => result.ChunkId));
        Assert.All(results, result =>
        {
            Assert.Equal(requestedCollectionId, result.CollectionId);
            Assert.Equal(documentId, result.DocumentId);
            Assert.Equal(currentVersionId, result.DocumentVersionId);
        });
        Assert.Equal([1, 2, 3], results.Select(result => result.ChunkOrdinal));
        Assert.Equal(["first current text", "second current text", "distant current text"], results.Select(result => result.ChunkText));
        Assert.Equal(0, results[0].CosineDistance, 10);
        Assert.Equal(0, results[1].CosineDistance, 10);
        Assert.Equal(1, results[2].CosineDistance, 10);
    }

    [Fact]
    public async Task Returns_empty_results_when_the_requested_collection_has_no_current_embeddings()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var collectionId = Guid.NewGuid();

        await using (var context = new IngestionDbContext(options))
        {
            context.Collections.Add(new Collection(
                collectionId,
                "Empty collection",
                DateTimeOffset.UtcNow,
                new EmbeddingProfile("ollama", "test:1", "1", 3)));
            await context.SaveChangesAsync();
        }

        await using var searchContext = new IngestionDbContext(options);
        var results = await new PostgresSemanticRetrievalRepository(searchContext).SearchAsync(
            [collectionId],
            [1, 0, 0],
            1,
            CancellationToken.None);

        Assert.Empty(results);
    }

    private DbContextOptions<IngestionDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;

    private static async Task ResetDatabaseAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE client_credentials, service_clients, operations, chunks, document_versions, documents, collections CASCADE;");
    }
}
