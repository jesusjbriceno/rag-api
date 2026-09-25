using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class OperationWorkerOptions
{
    public const string SectionName = "OperationWorker";

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public string? WorkerId { get; init; }
}

public sealed class OperationWorker(
    IOperationClaimRepository operationClaims,
    IOperationProcessor processor,
    IOptions<OperationWorkerOptions> options,
    ILogger<OperationWorker> logger,
    HistoricalTelemetry? telemetry = null,
    IOperationWorkloadClassifier? workloadClassifier = null) : BackgroundService
{
    private readonly OperationWorkerOptions _options = options.Value;
    private readonly HistoricalTelemetry _telemetry = telemetry ?? new HistoricalTelemetry();
    private readonly IOperationWorkloadClassifier _workloadClassifier = workloadClassifier ?? new DefaultOperationWorkloadClassifier();
    private readonly string _workerId = string.IsNullOrWhiteSpace(options.Value.WorkerId)
        ? $"{Environment.MachineName}:{Guid.NewGuid():N}"
        : options.Value.WorkerId.Trim();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimedAt = DateTimeOffset.UtcNow;
                var operation = await operationClaims.ClaimNextAsync(
                    _workerId,
                    claimedAt,
                    _options.LeaseDuration,
                    stoppingToken);

                if (operation is not null)
                {
                    var workloadClass = _workloadClassifier.Classify(operation);
                    _telemetry.Begin(operation.Id, workloadClass);
                    _telemetry.RecordQueueWait(operation.Id, QueueWait(operation, claimedAt));
                    try
                    {
                        var disposition = await processor.ProcessAsync(operation, stoppingToken);
                        logger.LogInformation(
                            "Operation {OperationId} processing disposition is {Disposition}.",
                            operation.Id,
                            disposition);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        _telemetry.Complete(operation.Id, OperationTerminalState.LeaseLost, DateTimeOffset.UtcNow);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The operation worker poll failed.");
            }

            await Task.Delay(_options.PollInterval, stoppingToken);
        }
    }

    private static TimeSpan QueueWait(Operation operation, DateTimeOffset claimedAt)
    {
        var startedAt = operation.StartedAt ?? claimedAt;
        var wait = startedAt - operation.CreatedAt;
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }
}
