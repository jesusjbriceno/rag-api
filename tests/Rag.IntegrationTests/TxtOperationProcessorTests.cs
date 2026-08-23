using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class TxtOperationProcessorTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Processes_txt_content_into_version_owned_chunks_and_marks_the_operation_succeeded()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        try
        {
            var content = Encoding.UTF8.GetBytes($"{new string('a', 1_800)}\r\n\r\n{new string('b', 500)}");
            var setup = await AddClaimedOperationAsync(options, rootPath, content, "worker-a");

            var disposition = await CreateProcessor(options, rootPath).ProcessAsync(setup.Operation, CancellationToken.None);

            Assert.Equal(OperationProcessingDisposition.Succeeded, disposition);
            await using var verificationContext = new IngestionDbContext(options);
            var persistedOperation = await verificationContext.Operations.SingleAsync(operation => operation.Id == setup.Operation.Id);
            var chunks = await verificationContext.Chunks
                .Where(chunk => chunk.DocumentVersionId == setup.Version.Id)
                .OrderBy(chunk => chunk.Ordinal)
                .ToListAsync();
            Assert.Equal(OperationStatus.Succeeded, persistedOperation.Status);
            Assert.Null(persistedOperation.LeaseOwner);
            Assert.Null(persistedOperation.LeaseExpiresAt);
            Assert.NotNull(persistedOperation.CompletedAt);
            Assert.Equal(new[] { 1, 2 }, chunks.Select(chunk => chunk.Ordinal));
            Assert.All(chunks, chunk => Assert.Equal(setup.Version.Id, chunk.DocumentVersionId));
            Assert.Equal(new string('a', TxtChunker.OverlapCharacters), chunks[1].Text[..TxtChunker.OverlapCharacters]);
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
    public async Task Invalid_utf8_content_marks_the_claimed_operation_failed_with_a_parse_stage()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        try
        {
            var setup = await AddClaimedOperationAsync(options, rootPath, [0xC3, 0x28], "worker-a");

            var disposition = await CreateProcessor(options, rootPath).ProcessAsync(setup.Operation, CancellationToken.None);

            Assert.Equal(OperationProcessingDisposition.Failed, disposition);
            await using var verificationContext = new IngestionDbContext(options);
            var persistedOperation = await verificationContext.Operations.SingleAsync(operation => operation.Id == setup.Operation.Id);
            Assert.Equal(OperationStatus.Failed, persistedOperation.Status);
            Assert.Equal("parse", persistedOperation.FailureStage);
            Assert.Contains("valid UTF-8", persistedOperation.FailureMessage);
            Assert.Null(persistedOperation.LeaseOwner);
            Assert.Null(persistedOperation.LeaseExpiresAt);
            Assert.Empty(await verificationContext.Chunks.ToListAsync());
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
    public async Task Utf8_content_with_a_nul_marks_the_operation_failed_without_chunks_or_reclaim()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        try
        {
            var setup = await AddClaimedOperationAsync(options, rootPath, [(byte)'a', 0, (byte)'b'], "worker-a");

            var disposition = await CreateProcessor(options, rootPath).ProcessAsync(setup.Operation, CancellationToken.None);

            Assert.Equal(OperationProcessingDisposition.Failed, disposition);
            await using (var verificationContext = new IngestionDbContext(options))
            {
                var persistedOperation = await verificationContext.Operations.SingleAsync(operation => operation.Id == setup.Operation.Id);
                Assert.Equal(OperationStatus.Failed, persistedOperation.Status);
                Assert.Equal("parse", persistedOperation.FailureStage);
                Assert.Contains("NUL", persistedOperation.FailureMessage);
                Assert.Null(persistedOperation.LeaseOwner);
                Assert.Null(persistedOperation.LeaseExpiresAt);
                Assert.Empty(await verificationContext.Chunks.ToListAsync());
            }

            var claims = new OperationClaimRepository(new TestDbContextFactory(options));
            var reclaimed = await claims.ClaimNextAsync(
                "worker-b",
                DateTimeOffset.UtcNow.AddHours(1),
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            Assert.Null(reclaimed);
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
    public async Task Stale_worker_cannot_write_chunks_or_complete_a_reclaimed_operation()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        try
        {
            var setup = await AddClaimedOperationAsync(options, rootPath, "stale worker content"u8.ToArray(), "worker-a");
            await ExpireLeaseAsync(setup.Operation.Id);
            var claims = new OperationClaimRepository(new TestDbContextFactory(options));
            var reclaimed = await claims.ClaimNextAsync("worker-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);

            var disposition = await CreateProcessor(options, rootPath).ProcessAsync(setup.Operation, CancellationToken.None);

            Assert.NotNull(reclaimed);
            Assert.Equal(setup.Operation.Id, reclaimed.Id);
            Assert.Equal(OperationProcessingDisposition.LeaseLost, disposition);
            await using var verificationContext = new IngestionDbContext(options);
            var persistedOperation = await verificationContext.Operations.SingleAsync(operation => operation.Id == setup.Operation.Id);
            Assert.Equal(OperationStatus.Running, persistedOperation.Status);
            Assert.Equal("worker-a", persistedOperation.LeaseOwner);
            Assert.Empty(await verificationContext.Chunks.ToListAsync());
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private TxtOperationProcessor CreateProcessor(DbContextOptions<IngestionDbContext> options, string rootPath) => new(
        new OperationCompletionRepository(new TestDbContextFactory(options)),
        new FileSystemImmutableContentStore(rootPath),
        new TxtChunker(),
        NullLogger<TxtOperationProcessor>.Instance);

    private DbContextOptions<IngestionDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

    private static async Task ResetDatabaseAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE operations, chunks, document_versions, documents, collections CASCADE;");
    }

    private async Task<(Operation Operation, DocumentVersion Version)> AddClaimedOperationAsync(
        DbContextOptions<IngestionDbContext> options,
        string rootPath,
        byte[] content,
        string workerId)
    {
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var reference = ContentReference.ForVersion(versionId);
        var hash = ContentHash.FromBytes(content);
        await new FileSystemImmutableContentStore(rootPath).StoreAsync(reference, hash, content, CancellationToken.None);

        await using (var context = new IngestionDbContext(options))
        {
            var collection = new Collection(Guid.NewGuid(), "TXT processing collection", now);
            var document = new Document(Guid.NewGuid(), collection.Id, $"source://{versionId:N}", now);
            var documentVersion = document.AddVersion(versionId, "content.txt", hash, reference, now);
            var operation = Operation.CreatePending(documentVersion.Id, now);
            context.Collections.Add(collection);
            context.Documents.Add(document);
            context.Operations.Add(operation);
            await context.SaveChangesAsync();
        }

        var claims = new OperationClaimRepository(new TestDbContextFactory(options));
        var claimed = await claims.ClaimNextAsync(workerId, now, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        await using var verificationContext = new IngestionDbContext(options);
        var persistedVersion = await verificationContext.DocumentVersions.SingleAsync(item => item.Id == versionId);
        return (claimed, persistedVersion);
    }

    private async Task ExpireLeaseAsync(Guid operationId)
    {
        await using var context = new IngestionDbContext(CreateOptions());
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE operations
            SET "LeaseExpiresAt" = clock_timestamp() - INTERVAL '1 second'
            WHERE "Id" = {operationId}
            """);
    }

    private sealed class TestDbContextFactory(DbContextOptions<IngestionDbContext> options) : IDbContextFactory<IngestionDbContext>
    {
        public IngestionDbContext CreateDbContext() => new(options);

        public Task<IngestionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionDbContext(options));
    }
}
