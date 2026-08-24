using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class IngestionReliabilityTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Concurrent_same_reference_and_content_returns_the_persisted_winner()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var collection = await AddCollectionAsync(options);
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");

        try
        {
            await using var firstContext = new IngestionDbContext(options);
            await using var secondContext = new IngestionDbContext(options);
            using var contentStore = new CoordinatedContentStore(new FileSystemImmutableContentStore(rootPath));
            var firstHandler = new AcceptTxtIngestionHandler(
                new IngestionRepository(firstContext, new TestDbContextFactory(options)),
                contentStore);
            var secondHandler = new AcceptTxtIngestionHandler(
                new IngestionRepository(secondContext, new TestDbContextFactory(options)),
                contentStore);
            var command = new AcceptTxtIngestionCommand(
                collection.Id,
                "guide.txt",
                "identical content"u8.ToArray(),
                "source://guide");

            var results = await Task.WhenAll(
                Task.Run(() => firstHandler.HandleAsync(command)),
                Task.Run(() => secondHandler.HandleAsync(command)));

            Assert.Single(results, result => !result.IsDuplicate);
            Assert.Single(results, result => result.IsDuplicate);
            Assert.Equal(results[0].DocumentId, results[1].DocumentId);
            Assert.Equal(results[0].DocumentVersionId, results[1].DocumentVersionId);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(rootPath, "versions")));

            var failedContext = results[0].IsDuplicate ? firstContext : secondContext;
            Assert.Empty(failedContext.ChangeTracker.Entries());
            await failedContext.SaveChangesAsync();

            await using var verificationContext = new IngestionDbContext(options);
            Assert.Equal(1, await verificationContext.Documents.CountAsync());
            Assert.Equal(1, await verificationContext.DocumentVersions.CountAsync());
            Assert.Equal(1, await verificationContext.Operations.CountAsync());
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PostgreSql_rejects_invalid_ingestion_invariants_from_direct_writes()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var first = await AddDocumentAsync(options, "source://first");
        var second = await AddDocumentAsync(options, "source://second");

        await using var context = new IngestionDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var invalidNumberException = await Record.ExceptionAsync(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO document_versions ("Id", "DocumentId", "Number", "FileName", "MimeType", "ContentHash", "ContentReference", "CreatedAt")
            VALUES ('{Guid.NewGuid()}', '{first.DocumentId}', 0, 'invalid.txt', 'text/plain', '{ContentHash.FromBytes("content"u8)}', 'versions/{Guid.NewGuid():N}.txt', '{now:O}');
            """));
        var invalidHashException = await Record.ExceptionAsync(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO document_versions ("Id", "DocumentId", "Number", "FileName", "MimeType", "ContentHash", "ContentReference", "CreatedAt")
            VALUES ('{Guid.NewGuid()}', '{first.DocumentId}', 2, 'invalid.txt', 'text/plain', '{new string('A', 64)}', 'versions/{Guid.NewGuid():N}.txt', '{now:O}');
            """));
        var invalidStatusException = await Record.ExceptionAsync(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO operations ("Id", "DocumentVersionId", "Status", "CreatedAt")
            VALUES ('{Guid.NewGuid()}', '{first.VersionId}', 'Unexpected', '{now:O}');
            """));
        var runningWithoutLeaseException = await Record.ExceptionAsync(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO operations ("Id", "DocumentVersionId", "Status", "CreatedAt")
            VALUES ('{Guid.NewGuid()}', '{first.VersionId}', 'Running', '{now:O}');
            """));
        var danglingCurrentVersionException = await Record.ExceptionAsync(() => context.Database.ExecuteSqlAsync(
            $"UPDATE documents SET \"CurrentVersionId\" = '{Guid.NewGuid()}' WHERE \"Id\" = '{first.DocumentId}';"));
        var crossDocumentCurrentVersionException = await Record.ExceptionAsync(() => context.Database.ExecuteSqlAsync(
            $"UPDATE documents SET \"CurrentVersionId\" = '{second.VersionId}' WHERE \"Id\" = '{first.DocumentId}';"));
        var currentVersionDeletionException = await Record.ExceptionAsync(() => context.Database.ExecuteSqlAsync(
            $"DELETE FROM document_versions WHERE \"Id\" = '{first.VersionId}';"));

        Assert.NotNull(invalidNumberException);
        Assert.NotNull(invalidHashException);
        Assert.NotNull(invalidStatusException);
        Assert.NotNull(runningWithoutLeaseException);
        Assert.NotNull(danglingCurrentVersionException);
        Assert.NotNull(crossDocumentCurrentVersionException);
        Assert.NotNull(currentVersionDeletionException);

        await context.Entry(first.Document).ReloadAsync();
        Assert.Equal(first.VersionId, first.Document.CurrentVersionId);
    }

    private DbContextOptions<IngestionDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;

    private static async Task ResetDatabaseAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE client_credentials, service_clients, operations, document_versions, documents, collections CASCADE;");
    }

    private static async Task<Collection> AddCollectionAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        var collection = new Collection(Guid.NewGuid(), "Integration collection", DateTimeOffset.UtcNow);
        context.Collections.Add(collection);
        await context.SaveChangesAsync();
        return collection;
    }

    private static async Task<(Document Document, Guid DocumentId, Guid VersionId)> AddDocumentAsync(
        DbContextOptions<IngestionDbContext> options,
        string externalReference)
    {
        await using var context = new IngestionDbContext(options);
        var collection = new Collection(Guid.NewGuid(), "Invariant collection", DateTimeOffset.UtcNow);
        var document = new Document(Guid.NewGuid(), collection.Id, externalReference, DateTimeOffset.UtcNow);
        var version = document.AddVersion(
            Guid.NewGuid(),
            "content.txt",
            ContentHash.FromBytes("content"u8),
            ContentReference.ForVersion(Guid.NewGuid()),
            DateTimeOffset.UtcNow);
        context.Collections.Add(collection);
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return (document, document.Id, version.Id);
    }

    private sealed class TestDbContextFactory(DbContextOptions<IngestionDbContext> options) : IDbContextFactory<IngestionDbContext>
    {
        public IngestionDbContext CreateDbContext() => new(options);

        public Task<IngestionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionDbContext(options));
    }

    private sealed class CoordinatedContentStore(IImmutableContentStore inner) : IImmutableContentStore, IDisposable
    {
        private readonly Barrier _barrier = new(participantCount: 2);

        public async Task StoreAsync(
            ContentReference reference,
            ContentHash contentHash,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            await inner.StoreAsync(reference, contentHash, content, cancellationToken);
            _barrier.SignalAndWait(cancellationToken);
        }

        public Task<byte[]> ReadAsync(ContentReference reference, ContentHash contentHash, CancellationToken cancellationToken) =>
            inner.ReadAsync(reference, contentHash, cancellationToken);

        public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken) =>
            inner.DeleteAsync(reference, cancellationToken);

        public void Dispose() => _barrier.Dispose();
    }
}
