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

public sealed class DeferredOperationProcessor(ILogger<DeferredOperationProcessor> logger) : IOperationProcessor
{
    public Task<OperationProcessingDisposition> ProcessAsync(Operation operation, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Operation {OperationId} was deferred because no content processor is implemented; its lease will expire for a future worker slice.",
            operation.Id);
        return Task.FromResult(OperationProcessingDisposition.Deferred);
    }
}

public sealed class OperationWorker(
    IOperationClaimRepository operationClaims,
    IOperationProcessor processor,
    IOptions<OperationWorkerOptions> options,
    ILogger<OperationWorker> logger) : BackgroundService
{
    private readonly OperationWorkerOptions _options = options.Value;
    private readonly string _workerId = string.IsNullOrWhiteSpace(options.Value.WorkerId)
        ? $"{Environment.MachineName}:{Guid.NewGuid():N}"
        : options.Value.WorkerId.Trim();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var operation = await operationClaims.ClaimNextAsync(
                    _workerId,
                    DateTimeOffset.UtcNow,
                    _options.LeaseDuration,
                    stoppingToken);

                if (operation is not null)
                {
                    var disposition = await processor.ProcessAsync(operation, stoppingToken);
                    logger.LogInformation(
                        "Operation {OperationId} processing disposition is {Disposition}.",
                        operation.Id,
                        disposition);
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
}
