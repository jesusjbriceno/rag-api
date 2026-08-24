using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class IngestionDbContextTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task PostgreSql_persists_the_ingestion_metadata_schema()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);

        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var collection = new Collection(Guid.NewGuid(), "Integration collection", now);
        var document = new Document(Guid.NewGuid(), collection.Id, "source://integration", now);
        var version = document.AddVersion(
            Guid.NewGuid(),
            "integration.txt",
            ContentHash.FromBytes("content"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            now);
        var operation = Operation.CreatePending(version.Id, now);
        context.Collections.Add(collection);
        context.Documents.Add(document);
        context.Operations.Add(operation);
        await context.SaveChangesAsync();

        Assert.True(await context.Collections.AnyAsync(item => item.Id == collection.Id));
        Assert.Equal(1, await context.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
        Assert.Equal(OperationStatus.Pending, await context.Operations.Where(item => item.Id == operation.Id).Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task PostgreSql_rejects_reparenting_a_document_current_version()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);

        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var collection = new Collection(Guid.NewGuid(), "Integration collection", now);
        var sourceDocument = new Document(Guid.NewGuid(), collection.Id, "source://first", now);
        var sourceVersion = sourceDocument.AddVersion(
            Guid.NewGuid(),
            "first.txt",
            ContentHash.FromBytes("first"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            now);
        var targetDocument = new Document(Guid.NewGuid(), collection.Id, "source://second", now);
        var targetVersion = targetDocument.AddVersion(
            Guid.NewGuid(),
            "second.txt",
            ContentHash.FromBytes("second"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            now);
        context.Collections.Add(collection);
        context.Documents.AddRange(sourceDocument, targetDocument);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE document_versions SET \"Number\" = 2 WHERE \"Id\" = {targetVersion.Id};");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE document_versions SET \"DocumentId\" = {targetDocument.Id} WHERE \"Id\" = {sourceVersion.Id};"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_documents_CurrentVersion_valid", exception.ConstraintName);
    }

    [Fact]
    public async Task PostgreSql_rejects_tabs_and_newlines_at_chunk_text_boundaries()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);

        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, chunks, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var collection = new Collection(Guid.NewGuid(), "Integration collection", now);
        var document = new Document(Guid.NewGuid(), collection.Id, "source://chunk-whitespace", now);
        var version = document.AddVersion(
            Guid.NewGuid(),
            "chunk-whitespace.txt",
            ContentHash.FromBytes("content"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            now);
        context.Collections.Add(collection);
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        foreach (var text in new[] { "\ttext", "text\t", "\ntext", "text\n" })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO chunks (\"Id\", \"DocumentVersionId\", \"Ordinal\", \"Text\") VALUES ({Guid.NewGuid()}, {version.Id}, 1, {text});"));

            Assert.Equal("23514", exception.SqlState);
            Assert.Equal("CK_chunks_Text_normalized", exception.ConstraintName);
        }
    }

    [Fact]
    public async Task PostgreSql_rejects_a_vector_with_dimensions_different_from_its_collection_profile()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);

        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, chunks, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var collection = new Collection(Guid.NewGuid(), "Embedding collection", now);
        var document = new Document(Guid.NewGuid(), collection.Id, "source://embedding", now);
        var version = document.AddVersion(
            Guid.NewGuid(),
            "embedding.txt",
            ContentHash.FromBytes("content"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            now);
        var chunk = new Chunk(Guid.NewGuid(), version.Id, 1, "embedding text");
        context.Collections.Add(collection);
        context.Documents.Add(document);
        context.Chunks.Add(chunk);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO chunk_embeddings (\"Id\", \"CollectionId\", \"ChunkId\", \"Values\") VALUES ({Guid.NewGuid()}, {collection.Id}, {chunk.Id}, '[1,2,3]'::vector);"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_chunk_embeddings_Dimensions_match_collection", exception.ConstraintName);
    }

    [Fact]
    public async Task PostgreSql_rejects_changes_to_a_collection_embedding_profile()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);

        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, chunks, document_versions, documents, collections CASCADE;");
        var collection = new Collection(Guid.NewGuid(), "Immutable embedding collection", DateTimeOffset.UtcNow);
        context.Collections.Add(collection);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE collections SET \"EmbeddingDimensions\" = 3 WHERE \"Id\" = {collection.Id};"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_collections_EmbeddingProfile_immutable", exception.ConstraintName);
    }

    [Fact]
    public async Task PostgreSql_rejects_reparenting_an_embedded_document_and_preserves_ownership()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);

        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, chunks, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var sourceCollection = new Collection(Guid.NewGuid(), "Source collection", now);
        var targetCollection = new Collection(Guid.NewGuid(), "Target collection", now);
        var document = new Document(Guid.NewGuid(), sourceCollection.Id, "source://embedded", now);
        var version = document.AddVersion(
            Guid.NewGuid(),
            "embedded.txt",
            ContentHash.FromBytes("content"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            now);
        var chunk = new Chunk(Guid.NewGuid(), version.Id, 1, "embedded text");
        var embedding = new ChunkEmbedding(
            Guid.NewGuid(),
            sourceCollection.Id,
            chunk.Id,
            Enumerable.Repeat(1f, EmbeddingProfile.Default.Dimensions).ToArray());
        context.Collections.AddRange(sourceCollection, targetCollection);
        context.Documents.Add(document);
        context.Chunks.Add(chunk);
        context.ChunkEmbeddings.Add(embedding);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE documents SET \"CollectionId\" = {targetCollection.Id} WHERE \"Id\" = {document.Id};"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_documents_Embedded_chunks_collection_immutable", exception.ConstraintName);
        context.ChangeTracker.Clear();
        Assert.Equal(sourceCollection.Id, await context.Documents.AsNoTracking()
            .Where(item => item.Id == document.Id)
            .Select(item => item.CollectionId)
            .SingleAsync());
        Assert.Equal(sourceCollection.Id, await context.ChunkEmbeddings.AsNoTracking()
            .Where(item => item.Id == embedding.Id)
            .Select(item => item.CollectionId)
            .SingleAsync());
    }

    [Fact]
    public async Task PostgreSql_permits_reparenting_a_document_without_embeddings()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);

        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, chunks, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var sourceCollection = new Collection(Guid.NewGuid(), "Source collection", now);
        var targetCollection = new Collection(Guid.NewGuid(), "Target collection", now);
        var document = new Document(Guid.NewGuid(), sourceCollection.Id, "source://unembedded", now);
        document.AddVersion(
            Guid.NewGuid(),
            "unembedded.txt",
            ContentHash.FromBytes("content"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            now);
        context.Collections.AddRange(sourceCollection, targetCollection);
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE documents SET \"CollectionId\" = {targetCollection.Id} WHERE \"Id\" = {document.Id};");

        context.ChangeTracker.Clear();
        Assert.Equal(targetCollection.Id, await context.Documents.AsNoTracking()
            .Where(item => item.Id == document.Id)
            .Select(item => item.CollectionId)
            .SingleAsync());
    }
}
