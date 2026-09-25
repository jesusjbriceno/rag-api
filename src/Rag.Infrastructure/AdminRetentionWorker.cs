using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed record AdminRetentionResult(int ReplaysPurged, int OperationsPurged, int AuditEventsPurged);

public sealed class AdminRetentionWorker(
    IDbContextFactory<IngestionDbContext> dbContextFactory,
    IOptions<AdminAuditOptions> auditOptions,
    IOptions<AdminOperationsOptions> operationsOptions,
    ILogger<AdminRetentionWorker> logger) : BackgroundService
{
    private static readonly AdminActor SystemActor = new("system", "retention-worker");

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly AdminAuditOptions _audit = auditOptions.Value;

    private readonly AdminOperationsOptions _operations = operationsOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await PurgeAsync(DateTimeOffset.UtcNow, stoppingToken);
                logger.LogInformation(
                    "Admin retention purge completed: {ReplaysPurged} replays, {OperationsPurged} operations, {AuditEventsPurged} audit events.",
                    result.ReplaysPurged,
                    result.OperationsPurged,
                    result.AuditEventsPurged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The admin retention purge failed.");
            }

            await Task.Delay(PurgeInterval, stoppingToken);
        }
    }

    public async Task<AdminRetentionResult> PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var operationCutoff = now.Subtract(TimeSpan.FromHours(_operations.RetentionHours));
        var auditCutoff = now.Subtract(TimeSpan.FromDays(_audit.RetentionDays));

        var replaysPurged = await dbContext.AdminAssertionReplays
            .Where(replay => replay.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
        var operationsPurged = await dbContext.AdminOperations
            .Where(operation => operation.CreatedAt <= operationCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var auditEventsPurged = 0;
        if (!_audit.IsIndefinite)
        {
            auditEventsPurged = await dbContext.AdminAuditEvents
                .CountAsync(auditEvent => auditEvent.OccurredAt <= auditCutoff, cancellationToken);
        }

        if (!_audit.IsIndefinite)
        {
            await dbContext.AdminAuditEvents
                .Where(auditEvent => auditEvent.OccurredAt <= auditCutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // The event occurs after the audit delete, so it is never a target of its own cycle.
        dbContext.AdminAuditEvents.Add(new AdminAuditEvent(
            Guid.NewGuid(),
            SystemActor,
            "retention_purge",
            "succeeded",
            now,
            targetType: "system",
            targetId: null,
            operation: null,
            allowlistedJson: JsonSerializer.Serialize(new
            {
                mode = _audit.NormalizedMode,
                retentionDays = _audit.RetentionDays,
                retentionHours = _operations.RetentionHours,
                replaysPurged,
                operationsPurged,
                auditEventsPurged,
            })));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AdminRetentionResult(replaysPurged, operationsPurged, auditEventsPurged);
    }
}
