using Microsoft.EntityFrameworkCore;
using Npgsql;
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
            .UseNpgsql(fixture.ConnectionString)
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
            .UseNpgsql(fixture.ConnectionString)
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
}
