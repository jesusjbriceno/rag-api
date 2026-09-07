using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AdminAuditDurabilityTests(PostgreSqlFixture fixture)
{
    private static readonly AdminActor Actor = new("cf-subject", "admin-app");

    [Fact]
    public async Task Audit_events_persist_across_a_fresh_context_restart()
    {
        var options = CreateOptions();
        await ResetAsync(options);

        await using (var context = new IngestionDbContext(options))
        {
            var repository = new AdminRepository(context);
            repository.AddAuditEvent(Actor, "create_client", "succeeded", DateTimeOffset.UtcNow, "client", "target-a", "op-1", null);
            repository.AddAuditEvent(Actor, "issue_credential", "succeeded", DateTimeOffset.UtcNow, "credential", "target-b", "op-2", null);
            await context.SaveChangesAsync();
        }

        // A brand-new context + repository (a simulated process restart) must still read both events.
        await using var restarted = new IngestionDbContext(options);
        var page = await new AdminRepository(restarted).ListAuditAsync(50, null, CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, item => item.Action == "create_client");
        Assert.Contains(page.Items, item => item.Action == "issue_credential");
        Assert.All(page.Items, item => Assert.Equal(Actor.ActorSubject, item.ActorSubject));
    }

    [Fact]
    public async Task Replay_reservation_survives_a_fresh_context_restart()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var preRestart = new AdminReplayRepository(new TestDbContextFactory(options));
        Assert.True(await preRestart.ReserveAsync("issuer", "jti-restart", "app-1", expiresAt, CancellationToken.None));

        // A fresh repository + factory (a simulated process restart) must still reject the same assertion.
        var postRestart = new AdminReplayRepository(new TestDbContextFactory(options));
        Assert.False(await postRestart.ReserveAsync("issuer", "jti-restart", "app-1", expiresAt, CancellationToken.None));
    }

    [Fact]
    public void Audit_events_expose_no_update_or_delete_surface()
    {
        var publicSetters = typeof(AdminAuditEvent)
            .GetProperties()
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .ToList();
        Assert.Empty(publicSetters);

        var auditMethods = typeof(IAdminRepository)
            .GetMethods()
            .Where(method => method.Name.Contains("Audit", StringComparison.Ordinal))
            .Select(method => method.Name)
            .ToList();
        Assert.Contains("AddAuditEvent", auditMethods);
        Assert.Contains("ListAuditAsync", auditMethods);
        Assert.DoesNotContain(auditMethods, name =>
            name.Contains("Update", StringComparison.Ordinal) ||
            name.Contains("Delete", StringComparison.Ordinal) ||
            name.Contains("Remove", StringComparison.Ordinal) ||
            name.Contains("Replace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_purge_is_auditable_and_its_event_survives_subsequent_purges()
    {
        var options = CreateOptions();
        await ResetAsync(options);
        var worker = new AdminRetentionWorker(
            new TestDbContextFactory(options),
            Options.Create(new AdminAuditOptions { RetentionMode = AdminAuditOptions.DaysMode, RetentionDays = 30 }),
            Options.Create(new AdminOperationsOptions { RetentionHours = 24 }),
            NullLogger<AdminRetentionWorker>.Instance);

        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        await using (var context = new IngestionDbContext(options))
        {
            context.AdminAuditEvents.Add(new AdminAuditEvent(
                Guid.NewGuid(), Actor, "create_client", "succeeded", now.AddDays(-31), "client", "target", null, null));
            await context.SaveChangesAsync();
        }

        await worker.PurgeAsync(now, CancellationToken.None);
        await worker.PurgeAsync(now.AddHours(1), CancellationToken.None);

        await using var verify = new IngestionDbContext(options);
        Assert.False(await verify.AdminAuditEvents.AnyAsync(auditEvent => auditEvent.Action == "create_client"));

        var retentionEvents = await verify.AdminAuditEvents
            .Where(auditEvent => auditEvent.Action == "retention_purge")
            .OrderBy(auditEvent => auditEvent.OccurredAt)
            .ToListAsync();
        Assert.Equal(2, retentionEvents.Count);
        Assert.All(retentionEvents, retentionEvent =>
        {
            Assert.Equal("system", retentionEvent.ActorSubject);
            Assert.Equal("retention-worker", retentionEvent.AppId);
            Assert.Equal("succeeded", retentionEvent.Outcome);
            Assert.DoesNotContain("secret", retentionEvent.AllowlistedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("assertion", retentionEvent.AllowlistedJson, StringComparison.OrdinalIgnoreCase);
        });
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

    private sealed class TestDbContextFactory(DbContextOptions<IngestionDbContext> options) : IDbContextFactory<IngestionDbContext>
    {
        public IngestionDbContext CreateDbContext() => new(options);

        public Task<IngestionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestionDbContext(options));
    }
}
