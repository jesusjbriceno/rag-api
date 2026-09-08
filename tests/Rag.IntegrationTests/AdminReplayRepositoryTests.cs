using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AdminReplayRepositoryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Sequential_reservations_for_the_same_assertion_return_true_then_false()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        var repository = new AdminReplayRepository(new TestDbContextFactory(options));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var first = await repository.ReserveAsync("issuer", "jti-1", "app-1", expiresAt, CancellationToken.None);
        var replay = await repository.ReserveAsync("issuer", "jti-1", "app-1", expiresAt, CancellationToken.None);

        Assert.True(first);
        Assert.False(replay);
    }

    [Fact]
    public async Task Distinct_assertions_reserve_independently()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        var repository = new AdminReplayRepository(new TestDbContextFactory(options));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        Assert.True(await repository.ReserveAsync("issuer", "jti-1", "app-1", expiresAt, CancellationToken.None));
        Assert.True(await repository.ReserveAsync("issuer", "jti-2", "app-1", expiresAt, CancellationToken.None));
        Assert.True(await repository.ReserveAsync("other-issuer", "jti-1", "app-1", expiresAt, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_reservations_for_the_same_assertion_yield_exactly_one_winner()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        var repository = new AdminReplayRepository(new TestDbContextFactory(options));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            repository.ReserveAsync("issuer", "jti-1", "app-1", expiresAt, CancellationToken.None))));

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(7, results.Count(result => !result));
    }

    private DbContextOptions<IngestionDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;

    private static async Task ResetAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE admin_assertion_replays;");
    }

    private sealed class TestDbContextFactory(DbContextOptions<IngestionDbContext> options) : IDbContextFactory<IngestionDbContext>
    {
        public IngestionDbContext CreateDbContext() => new(options);

        public Task<IngestionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionDbContext(options));
    }
}
