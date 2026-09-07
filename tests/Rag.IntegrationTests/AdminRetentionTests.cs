using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AdminRetentionTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Days_mode_purges_expired_replays_stale_operations_and_retained_audit_events()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        await SeedAsync(options);
        var worker = BuildWorker(
            options,
            new AdminAuditOptions { RetentionMode = AdminAuditOptions.DaysMode, RetentionDays = 30 },
            new AdminOperationsOptions { RetentionHours = 24 });

        var result = await worker.PurgeAsync(Now, CancellationToken.None);

        Assert.Equal(1, result.ReplaysPurged);
        Assert.Equal(1, result.OperationsPurged);
        Assert.Equal(1, result.AuditEventsPurged);

        await using var context = new IngestionDbContext(options);
        Assert.False(await context.AdminAssertionReplays.AnyAsync(replay => replay.Jti == "expired-replay"));
        Assert.True(await context.AdminAssertionReplays.AnyAsync(replay => replay.Jti == "future-replay"));
        Assert.False(await context.AdminOperations.AnyAsync(operation => operation.IdempotencyKey == "stale-operation"));
        Assert.True(await context.AdminOperations.AnyAsync(operation => operation.IdempotencyKey == "recent-operation"));
        Assert.False(await context.AdminAuditEvents.AnyAsync(auditEvent => auditEvent.Action == "create_client"));
        Assert.Equal(2, await context.AdminAuditEvents.CountAsync());

        var retentionEvent = await context.AdminAuditEvents.SingleAsync(auditEvent => auditEvent.Action == "retention_purge");
        Assert.Equal("system", retentionEvent.ActorSubject);
        Assert.Equal("retention-worker", retentionEvent.AppId);
        Assert.Equal("succeeded", retentionEvent.Outcome);
        Assert.DoesNotContain("secret", retentionEvent.AllowlistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", retentionEvent.AllowlistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assertion", retentionEvent.AllowlistedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Indefinite_mode_purges_replays_and_operations_but_preserves_audit_events()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        await SeedAsync(options);
        var worker = BuildWorker(
            options,
            new AdminAuditOptions { RetentionMode = AdminAuditOptions.IndefiniteMode, RetentionDays = 0 },
            new AdminOperationsOptions { RetentionHours = 24 });

        var result = await worker.PurgeAsync(Now, CancellationToken.None);

        Assert.Equal(1, result.ReplaysPurged);
        Assert.Equal(1, result.OperationsPurged);
        Assert.Equal(0, result.AuditEventsPurged);

        await using var context = new IngestionDbContext(options);
        Assert.False(await context.AdminAssertionReplays.AnyAsync(replay => replay.Jti == "expired-replay"));
        Assert.False(await context.AdminOperations.AnyAsync(operation => operation.IdempotencyKey == "stale-operation"));
        Assert.True(await context.AdminAuditEvents.AnyAsync(auditEvent => auditEvent.Action == "create_client"));
        Assert.Equal(3, await context.AdminAuditEvents.CountAsync());
    }

    [Fact]
    public async Task Failed_audit_delete_does_not_commit_a_successful_retention_event()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        await SeedAsync(options);
        var worker = BuildWorker(
            options,
            new AdminAuditOptions { RetentionMode = AdminAuditOptions.DaysMode, RetentionDays = 30 },
            new AdminOperationsOptions { RetentionHours = 24 });
        await AddAuditDeleteFailureTriggerAsync(options);

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => worker.PurgeAsync(Now, CancellationToken.None));
        }
        finally
        {
            await RemoveAuditDeleteFailureTriggerAsync(options);
        }

        await using var context = new IngestionDbContext(options);
        Assert.True(await context.AdminAssertionReplays.AnyAsync(replay => replay.Jti == "expired-replay"));
        Assert.True(await context.AdminOperations.AnyAsync(operation => operation.IdempotencyKey == "stale-operation"));
        Assert.True(await context.AdminAuditEvents.AnyAsync(auditEvent => auditEvent.Action == "create_client"));
        Assert.False(await context.AdminAuditEvents.AnyAsync(auditEvent => auditEvent.Action == "retention_purge" && auditEvent.Outcome == "succeeded"));
    }

    private static AdminRetentionWorker BuildWorker(
        DbContextOptions<IngestionDbContext> options,
        AdminAuditOptions audit,
        AdminOperationsOptions operations) =>
        new(
            new TestDbContextFactory(options),
            Options.Create(audit),
            Options.Create(operations),
            NullLogger<AdminRetentionWorker>.Instance);

    private static async Task SeedAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        context.AdminAssertionReplays.AddRange(
            new AdminAssertionReplay(Guid.NewGuid(), "issuer", "expired-replay", "app-1", Now.AddHours(-1), Now.AddHours(-2)),
            new AdminAssertionReplay(Guid.NewGuid(), "issuer", "future-replay", "app-1", Now.AddHours(1), Now.AddHours(-1)));
        context.AdminOperations.AddRange(
            new AdminOperation(Guid.NewGuid(), "app-1", "stale-operation", "fingerprint", AdminOperation.CompletedState, Now.AddHours(-25)),
            new AdminOperation(Guid.NewGuid(), "app-1", "recent-operation", "fingerprint", AdminOperation.CompletedState, Now.AddHours(-1)));
        context.AdminAuditEvents.AddRange(
            new AdminAuditEvent(Guid.NewGuid(), new AdminActor("cf-subject", "admin-app"), "create_client", "succeeded", Now.AddDays(-31), "client", "target", null, null),
            new AdminAuditEvent(Guid.NewGuid(), new AdminActor("cf-subject", "admin-app"), "issue_credential", "succeeded", Now.AddDays(-1), "credential", "target", null, null));
        await context.SaveChangesAsync();
    }

    private DbContextOptions<IngestionDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;

    private static async Task ResetAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE admin_assertion_replays, admin_operations, admin_audit_events;");
    }

    private static async Task AddAuditDeleteFailureTriggerAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION fail_admin_audit_delete()
            RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'Forced audit delete failure.';
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_admin_audit_delete
            BEFORE DELETE ON admin_audit_events
            FOR EACH ROW
            EXECUTE FUNCTION fail_admin_audit_delete();
            """);
    }

    private static async Task RemoveAuditDeleteFailureTriggerAsync(DbContextOptions<IngestionDbContext> options)
    {
        await using var context = new IngestionDbContext(options);
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_admin_audit_delete ON admin_audit_events;");
        await context.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS fail_admin_audit_delete();");
    }

    private sealed class TestDbContextFactory(DbContextOptions<IngestionDbContext> options) : IDbContextFactory<IngestionDbContext>
    {
        public IngestionDbContext CreateDbContext() => new(options);

        public Task<IngestionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionDbContext(options));
    }
}
