using System.Diagnostics;

namespace Rag.HistoricalLoader.Core.Benchmark;

/// <summary>Monotonic clock abstraction so sustained runs are testable without real wall time.</summary>
public interface IBenchmarkClock
{
    TimeSpan Elapsed { get; }
}

public delegate Task<PhaseResult> CandidatePhase(BenchmarkCandidate candidate, CancellationToken cancellationToken);

public delegate ResourceSnapshot ResourceSampler(TimeSpan elapsed);

/// <summary>
/// Thin probe that measures per-candidate discovery, snapshot, extraction, and staging
/// durations around injectable phase operations, samples resources, and loops until the
/// predeclared sustained duration elapses. It does not reference any production extractor.
/// </summary>
public sealed class ExtractionProbe
{
    private readonly ProbeOptions _options;
    private readonly IBenchmarkClock _clock;

    public ExtractionProbe(ProbeOptions options, IBenchmarkClock? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? new StopwatchClock();
    }

    public async Task<ProbeRun> RunAsync(
        IReadOnlyList<BenchmarkCandidate> candidates,
        CandidatePhase discover,
        CandidatePhase snapshot,
        CandidatePhase extract,
        CandidatePhase stage,
        ResourceSampler sampler,
        Func<BenchmarkObservation, CancellationToken, Task>? observationSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(extract);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(sampler);

        var observations = new List<BenchmarkObservation>();
        var started = _clock.Elapsed;

        while (candidates.Count > 0)
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation = await ProbeOneAsync(candidate, discover, snapshot, extract, stage, sampler, cancellationToken);
                observations.Add(observation);
                if (observationSink is not null)
                {
                    await observationSink(observation, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_clock.Elapsed - started >= _options.SustainedDuration)
            {
                break;
            }
        }

        return new ProbeRun(observations, _clock.Elapsed - started);
    }

    public static BenchmarkOutcome ClassifyExtractionOutcome(string outcome, TimeSpan duration, ProbeOptions options)
    {
        if (string.Equals(outcome, "hang", StringComparison.Ordinal) || duration >= options.HangThreshold)
        {
            return BenchmarkOutcome.Hang;
        }

        if (string.Equals(outcome, "timeout", StringComparison.Ordinal) || duration >= options.TimeoutThreshold)
        {
            return BenchmarkOutcome.Timeout;
        }

        return string.Equals(outcome, "completed", StringComparison.Ordinal)
            ? BenchmarkOutcome.Completed
            : BenchmarkOutcome.Error;
    }

    private async Task<BenchmarkObservation> ProbeOneAsync(
        BenchmarkCandidate candidate,
        CandidatePhase discover,
        CandidatePhase snapshot,
        CandidatePhase extract,
        CandidatePhase stage,
        ResourceSampler sampler,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var resource = sampler(_clock.Elapsed);

        var discoveryStart = _clock.Elapsed;
        await discover(candidate, cancellationToken);
        var discovery = _clock.Elapsed - discoveryStart;

        var snapshotStart = _clock.Elapsed;
        var snapshotResult = await snapshot(candidate, cancellationToken);
        var snapshotDuration = _clock.Elapsed - snapshotStart;

        var extractionStart = _clock.Elapsed;
        var extractionResult = await extract(candidate, cancellationToken);
        var extractionDuration = _clock.Elapsed - extractionStart;

        var stagingStart = _clock.Elapsed;
        await stage(candidate, cancellationToken);
        var stagingDuration = _clock.Elapsed - stagingStart;

        var outcome = ClassifyExtractionOutcome(extractionResult.Outcome, extractionDuration, _options);

        return new BenchmarkObservation(
            Guid.NewGuid(),
            _options.SampleSetId,
            candidate.CandidateId,
            candidate.StratumKey,
            startedAt,
            discovery,
            snapshotDuration,
            extractionDuration,
            stagingDuration,
            snapshotResult.Bytes,
            extractionResult.Bytes,
            outcome,
            extractionResult.ErrorCode,
            resource);
    }

    private sealed class StopwatchClock : IBenchmarkClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TimeSpan Elapsed => _stopwatch.Elapsed;
    }
}
