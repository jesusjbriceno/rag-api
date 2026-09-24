using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AdminReplayRepositoryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task PostgreSql_reserves_an_assertion_and_rejects_an_exact_replay()
    {
        var options = CreateOptions();
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE admin_assertion_replays;");
        var repository = new AdminReplayRepository(context);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);

        var reserved = await repository.TryReserveAsync("issuer-1", "jti-1", "app-1", expiresAt, CancellationToken.None);
        var replayed = await repository.TryReserveAsync("issuer-1", "jti-1", "app-1", expiresAt, CancellationToken.None);
        var distinct = await repository.TryReserveAsync("issuer-1", "jti-2", "app-1", expiresAt, CancellationToken.None);

        Assert.True(reserved);
        Assert.False(replayed);
        Assert.True(distinct);
    }

    [Fact]
    public async Task PostgreSql_reserves_a_replay_atomically_under_concurrent_attempts()
    {
        var options = CreateOptions();
        await using var setupContext = new IngestionDbContext(options);
        await setupContext.Database.MigrateAsync();
        await setupContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE admin_assertion_replays;");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);

        await using var firstContext = new IngestionDbContext(options);
        await using var secondContext = new IngestionDbContext(options);
        var results = await Task.WhenAll(
            new AdminReplayRepository(firstContext).TryReserveAsync("issuer-2", "jti-1", "app-1", expiresAt, CancellationToken.None),
            new AdminReplayRepository(secondContext).TryReserveAsync("issuer-2", "jti-1", "app-1", expiresAt, CancellationToken.None));

        Assert.Equal(1, results.Count(result => result));
    }

    private DbContextOptions<IngestionDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
}
