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
            var contentStore = new FileSystemImmutableContentStore(rootPath);
            var firstHandler = new AcceptTxtIngestionHandler(
                new IngestionRepository(firstContext),
                contentStore);
            var secondHandler = new AcceptTxtIngestionHandler(
                new IngestionRepository(secondContext),
                contentStore);
            var command = new AcceptTxtIngestionCommand(
                collection.ServiceClientId,
                collection.Id,
                "guide.txt",
                "identical content"u8.ToArray(),
                "source://guide");

            var results = await SubmitConcurrentlyAsync(
                () => firstHandler.HandleAsync(command),
                () => secondHandler.HandleAsync(command));

            Assert.Single(results, result => !result.IsDuplicate);
            Assert.Single(results, result => result.IsDuplicate);
            Assert.Equal(results[0].DocumentId, results[1].DocumentId);
            Assert.Equal(results[0].DocumentVersionId, results[1].DocumentVersionId);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(rootPath, "versions")));

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
    public async Task Concurrent_same_reference_and_different_content_creates_two_versions_and_operations_without_a_unique_failure()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var collection = await AddCollectionAsync(options);
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");

        try
        {
            await using var firstContext = new IngestionDbContext(options);
            await using var secondContext = new IngestionDbContext(options);
            var contentStore = new FileSystemImmutableContentStore(rootPath);
            var firstHandler = new AcceptTxtIngestionHandler(
                new IngestionRepository(firstContext),
                contentStore);
            var secondHandler = new AcceptTxtIngestionHandler(
                new IngestionRepository(secondContext),
                contentStore);

            var results = await SubmitConcurrentlyAsync(
                () => firstHandler.HandleAsync(new AcceptTxtIngestionCommand(
                    collection.ServiceClientId,
                    collection.Id,
                    "guide.txt",
                    "first content"u8.ToArray(),
                    "source://guide")),
                () => secondHandler.HandleAsync(new AcceptTxtIngestionCommand(
                    collection.ServiceClientId,
                    collection.Id,
                    "guide.txt",
                    "second content"u8.ToArray(),
                    "source://guide")));

            Assert.All(results, result => Assert.False(result.IsDuplicate));
            Assert.Equal(results[0].DocumentId, results[1].DocumentId);
            Assert.NotEqual(results[0].DocumentVersionId, results[1].DocumentVersionId);
            Assert.All(results, result => Assert.NotNull(result.OperationId));
            Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(rootPath, "versions")).Count());

            await using var verificationContext = new IngestionDbContext(options);
            var document = await verificationContext.Documents
                .Include(item => item.Versions)
                .SingleAsync();
            var operations = await verificationContext.Operations.ToListAsync();
            Assert.Equal([1, 2], document.Versions.Select(version => version.Number).Order().ToArray());
            Assert.Equal(2, operations.Count);
            Assert.All(operations, operation => Assert.Equal(OperationStatus.Pending, operation.Status));
            Assert.Equal(
                document.Versions.Select(version => version.Id).Order().ToArray(),
                operations.Select(operation => operation.DocumentVersionId).Order().ToArray());
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
        var collection = IntegrationData.NewCollection(context, "Integration collection", DateTimeOffset.UtcNow);
        context.Collections.Add(collection);
        await context.SaveChangesAsync();
        return collection;
    }

    private static async Task<(Document Document, Guid DocumentId, Guid VersionId)> AddDocumentAsync(
        DbContextOptions<IngestionDbContext> options,
        string externalReference)
    {
        await using var context = new IngestionDbContext(options);
        var collection = IntegrationData.NewCollection(context, "Invariant collection", DateTimeOffset.UtcNow);
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

    private static async Task<T[]> SubmitConcurrentlyAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        using var start = new Barrier(participantCount: 2);
        var firstSubmission = Task.Run(async () =>
        {
            start.SignalAndWait();
            return await first();
        });
        var secondSubmission = Task.Run(async () =>
        {
            start.SignalAndWait();
            return await second();
        });
        return await Task.WhenAll(firstSubmission, secondSubmission);
    }

}
