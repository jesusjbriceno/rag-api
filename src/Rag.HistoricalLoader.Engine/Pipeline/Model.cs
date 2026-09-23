using System.Text.Json;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;

namespace Rag.HistoricalLoader.Engine.Pipeline;

/// <summary>
/// Secret-free run configuration for the bounded pipeline. Watermark values and concurrency are chosen
/// from inventory/operator disk-budget evidence and persisted into the run's configuration snapshot;
/// no credential or token material is ever carried here.
/// </summary>
public sealed record PipelineOptions(
    int Concurrency,
    long StagedByteWatermark,
    int StagedCountWatermark)
{
    public string BuildConfigurationSnapshot()
        => JsonSerializer.Serialize(new
        {
            Concurrency,
            StagedByteWatermark,
            StagedCountWatermark,
        });
}

/// <summary>Stable failure classification per the design retry/failure table.</summary>
public enum FailureClass
{
    None,
    TransientNetwork,
    Authentication,
    Document,
    ContractData,
    LocalCapacity,
}

/// <summary>
/// Maps collaborator outcomes onto the design's five retry classes. HTTP transient/unknown outcomes are
/// network; auth outcomes are authentication; contract outcomes are contract/data; extraction failures
/// are document; any other thrown exception during a run is a local-capacity/infrastructure failure.
/// </summary>
public static class FailureClassifier
{
    public static FailureClass ClassifyApiOutcome(ApiOutcome outcome) => outcome switch
    {
        ApiOutcome.Success => FailureClass.None,
        ApiOutcome.TransientFailure => FailureClass.TransientNetwork,
        ApiOutcome.UnknownOutcome => FailureClass.TransientNetwork,
        ApiOutcome.AuthFailure => FailureClass.Authentication,
        ApiOutcome.ContractFailure => FailureClass.ContractData,
        _ => FailureClass.ContractData,
    };

    public static FailureClass ClassifyExtractionOutcome(ExtractionOutcome outcome) => outcome switch
    {
        ExtractionOutcome.Completed => FailureClass.None,
        ExtractionOutcome.SkippedDocument => FailureClass.Document,
        ExtractionOutcome.Error => FailureClass.Document,
        _ => FailureClass.Document,
    };

    public static FailureClass ClassifyException(Exception exception) => exception switch
    {
        OperationCanceledException => FailureClass.None,
        _ => FailureClass.LocalCapacity,
    };
}

/// <summary>
/// Full-jitter exponential backoff for transient network retries. Honours a server-provided
/// <c>Retry-After</c> up to a caller-supplied bound; otherwise returns a jittered delay in
/// <c>[0, min(baseDelay * 2^(attempt-1), maxDelay)]</c>.
/// </summary>
public static class RetryBackoff
{
    public static TimeSpan FullJitter(
        TimeSpan baseDelay,
        int dispatchedAttempts,
        TimeSpan maxDelay,
        TimeSpan? retryAfter = null,
        Random? random = null)
    {
        if (retryAfter is { } after && after > TimeSpan.Zero)
        {
            return after > maxDelay ? maxDelay : after;
        }

        var rng = random ?? Random.Shared;
        var attempt = Math.Max(1, dispatchedAttempts);
        var exponential = baseDelay.Multiply(Math.Pow(2, attempt - 1));
        var cap = exponential > maxDelay ? maxDelay : exponential;
        var jitterTicks = rng.NextInt64(0, cap.Ticks + 1);
        return TimeSpan.FromTicks(jitterTicks);
    }
}

/// <summary>Terminal summary of one bounded pipeline run.</summary>
public sealed record PipelineRunResult(
    int ClaimedCount,
    int LoadedCount,
    int SkippedCount,
    int RetryExhaustedCount,
    int BlockedAuthCount,
    int BlockedOperatorActionCount,
    long CommittedBytes,
    int CommittedCount,
    bool Paused,
    bool Completed,
    FailureClass? BlockReason = null);
