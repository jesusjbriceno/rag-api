using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class OperationClaimRepositoryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Concurrent_workers_claim_each_pending_operation_once()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var now = DateTimeOffset.UtcNow;
        var operations = await AddOperationsAsync(options, now, count: 2);
        var repository = new OperationClaimRepository(new TestDbContextFactory(options));
        using var barrier = new Barrier(participantCount: 4);
        var leaseStart = await GetDatabaseTimeAsync();

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repository.ClaimNextAsync($"worker-{worker}", now, TimeSpan.FromMinutes(5), CancellationToken.None);
        })));
        var leaseEnd = await GetDatabaseTimeAsync();

        var claimedOperations = claims.Where(operation => operation is not null).Cast<Operation>().ToArray();
        Assert.Equal(2, claimedOperations.Length);
        Assert.Equal(2, claimedOperations.Select(operation => operation.Id).Distinct().Count());
        Assert.All(claimedOperations, operation =>
        {
            Assert.Equal(OperationStatus.Running, operation.Status);
            Assert.NotNull(operation.LeaseOwner);
            Assert.InRange(
                operation.LeaseExpiresAt!.Value,
                leaseStart.AddMinutes(5),
                leaseEnd.AddMinutes(5));
        });
        Assert.Equal(operations.OrderBy(operation => operation).ToArray(), claimedOperations.Select(operation => operation.Id).OrderBy(operation => operation).ToArray());
    }

    [Fact]
    public async Task Claim_skips_a_row_locked_by_another_transaction()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var now = DateTimeOffset.UtcNow;
        var operations = await AddOperationsAsync(options, now, count: 2);
        var firstOperationId = operations[0];
        var secondOperationId = operations[1];
        var repository = new OperationClaimRepository(new TestDbContextFactory(options));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM operations WHERE \"Id\" = @id FOR UPDATE;", connection, transaction))
        {
            command.Parameters.AddWithValue("id", firstOperationId);
            await command.ExecuteNonQueryAsync();
        }

        var claimed = await repository.ClaimNextAsync("worker-a", now, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(secondOperationId, claimed.Id);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Expired_running_operation_is_reclaimed_with_a_new_lease()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var claimedAt = DateTimeOffset.UtcNow;
        var operationId = Assert.Single(await AddOperationsAsync(options, claimedAt, count: 1));
        var repository = new OperationClaimRepository(new TestDbContextFactory(options));

        var firstClaim = await repository.ClaimNextAsync("worker-a", claimedAt, TimeSpan.FromMinutes(1), CancellationToken.None);
        await ExpireLeaseAsync(operationId);
        var leaseStart = await GetDatabaseTimeAsync();
        var reclaimed = await repository.ClaimNextAsync("worker-b", claimedAt.AddMinutes(2), TimeSpan.FromMinutes(5), CancellationToken.None);
        var leaseEnd = await GetDatabaseTimeAsync();

        Assert.NotNull(firstClaim);
        Assert.NotNull(reclaimed);
        Assert.Equal(operationId, reclaimed.Id);
        Assert.Equal("worker-b", reclaimed.LeaseOwner);
        Assert.InRange(
            reclaimed.LeaseExpiresAt!.Value,
            leaseStart.AddMinutes(5),
            leaseEnd.AddMinutes(5));

        await using var verificationContext = new IngestionDbContext(options);
        var persisted = await verificationContext.Operations.SingleAsync(operation => operation.Id == operationId);
        Assert.Equal("worker-b", persisted.LeaseOwner);
        Assert.InRange(
            persisted.LeaseExpiresAt!.Value,
            leaseStart.AddMinutes(5),
            leaseEnd.AddMinutes(5));
    }

    [Fact]
    public async Task Severely_skewed_caller_timestamps_cannot_reclaim_or_hide_a_lease()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var operationId = Assert.Single(await AddOperationsAsync(options, DateTimeOffset.UtcNow, count: 1));
        var repository = new OperationClaimRepository(new TestDbContextFactory(options));

        var initialClaim = await repository.ClaimNextAsync(
            "worker-a",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        var prematureReclaim = await repository.ClaimNextAsync(
            "worker-b",
            DateTimeOffset.UtcNow.AddYears(50),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.NotNull(initialClaim);
        Assert.Null(prematureReclaim);

        await ExpireLeaseAsync(operationId);

        var reclaimed = await repository.ClaimNextAsync(
            "worker-c",
            DateTimeOffset.UtcNow.AddYears(-50),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.NotNull(reclaimed);
        Assert.Equal(operationId, reclaimed.Id);
        Assert.Equal("worker-c", reclaimed.LeaseOwner);
    }

    [Fact]
    public async Task Hosted_worker_claims_and_invokes_its_processor()
    {
        var options = CreateOptions();
        await ResetDatabaseAsync(options);
        var now = DateTimeOffset.UtcNow;
        var operationId = Assert.Single(await AddOperationsAsync(options, now, count: 1));
        var processor = new RecordingProcessor();
        var worker = new OperationWorker(
            new OperationClaimRepository(new TestDbContextFactory(options)),
            processor,
            Options.Create(new OperationWorkerOptions
            {
                LeaseDuration = TimeSpan.FromMinutes(5),
                PollInterval = TimeSpan.FromMilliseconds(10),
                WorkerId = "integration-worker",
            }),
            NullLogger<OperationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var processedOperationId = await processor.ProcessedOperationId.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(operationId, processedOperationId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var verificationContext = new IngestionDbContext(options);
        var persisted = await verificationContext.Operations.SingleAsync(operation => operation.Id == operationId);
        Assert.Equal(OperationStatus.Running, persisted.Status);
        Assert.Equal("integration-worker", persisted.LeaseOwner);
        Assert.NotNull(persisted.LeaseExpiresAt);
        Assert.Null(persisted.CompletedAt);
    }

    private DbContextOptions<IngestionDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;

    private async Task<DateTimeOffset> GetDatabaseTimeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection);
        var result = await command.ExecuteScalarAsync();
        return result switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("PostgreSQL did not return a timestamp."),
        };
    }

    private async Task ExpireLeaseAsync(Guid operationId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE operations SET \"LeaseExpiresAt\" = clock_timestamp() - INTERVAL '1 second' WHERE \"Id\" = @id;",
            connection);
        command.Parameters.AddWithValue("id", operationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ResetDatabaseAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE client_credentials, service_clients, operations, document_versions, documents, collections CASCADE;");
    }

    private static async Task<Guid[]> AddOperationsAsync(
        DbContextOptions<IngestionDbContext> options,
        DateTimeOffset createdAt,
        int count)
    {
        await using var context = new IngestionDbContext(options);
        var collection = new Collection(Guid.NewGuid(), "Operation collection", createdAt);
        context.Collections.Add(collection);
        var operationIds = new List<Guid>(count);
        for (var index = 0; index < count; index++)
        {
            var createdAtForOperation = createdAt.AddSeconds(index);
            var document = new Document(Guid.NewGuid(), collection.Id, $"source://operation-{index}", createdAtForOperation);
            var version = document.AddVersion(
                Guid.NewGuid(),
                $"operation-{index}.txt",
                ContentHash.FromBytes("content"u8),
                ContentReference.ForVersion(Guid.NewGuid()),
                createdAtForOperation);
            var operation = Operation.CreatePending(version.Id, createdAtForOperation);
            context.Documents.Add(document);
            context.Operations.Add(operation);
            operationIds.Add(operation.Id);
        }

        await context.SaveChangesAsync();
        return operationIds.ToArray();
    }

    private sealed class TestDbContextFactory(DbContextOptions<IngestionDbContext> options) : IDbContextFactory<IngestionDbContext>
    {
        public IngestionDbContext CreateDbContext() => new(options);

        public Task<IngestionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionDbContext(options));
    }

    private sealed class RecordingProcessor : IOperationProcessor
    {
        public TaskCompletionSource<Guid> ProcessedOperationId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OperationProcessingDisposition> ProcessAsync(Operation operation, CancellationToken cancellationToken)
        {
            ProcessedOperationId.TrySetResult(operation.Id);
            return Task.FromResult(OperationProcessingDisposition.LeaseLost);
        }
    }
}
