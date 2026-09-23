namespace Rag.Domain;

public enum OperationWorkloadClass
{
    RealTime,
    Historical,
}

public enum OperationTerminalState
{
    Succeeded,
    Failed,
    LeaseLost,
}

public sealed record OperationQueueGauge(
    int ActiveRealTime,
    int PendingRealTime,
    int ActiveHistorical,
    int PendingHistorical);

public sealed record OperationTelemetrySample(
    Guid OperationId,
    OperationWorkloadClass WorkloadClass,
    TimeSpan QueueWait,
    int ChunkCount,
    TimeSpan ChunkingDuration,
    int EmbeddingRequestCount,
    TimeSpan EmbeddingDuration,
    TimeSpan IndexingDuration,
    OperationTerminalState TerminalState,
    DateTimeOffset CompletedAt);

public sealed record RealTimeQueueImpact(
    int SampleCount,
    TimeSpan TotalQueueWait,
    TimeSpan MaximumQueueWait,
    TimeSpan AverageQueueWait);

public sealed record HistoricalTelemetrySnapshot(
    IReadOnlyList<OperationTelemetrySample> Samples,
    int TotalRealTimeOperations,
    int TotalHistoricalOperations,
    OperationQueueGauge QueueGauge,
    RealTimeQueueImpact RealTimeQueueImpact);

public interface IOperationWorkloadClassifier
{
    OperationWorkloadClass Classify(Operation operation);
}

public sealed class DefaultOperationWorkloadClassifier : IOperationWorkloadClassifier
{
    public OperationWorkloadClass Classify(Operation operation) => OperationWorkloadClass.RealTime;
}

public sealed class HistoricalTelemetry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OperationAccumulator> _inFlight = new();
    private readonly List<OperationTelemetrySample> _samples = new();
    private int _totalRealTimeOperations;
    private int _totalHistoricalOperations;
    private int _activeRealTime;
    private int _pendingRealTime;
    private int _activeHistorical;
    private int _pendingHistorical;

    public void Begin(Guid operationId, OperationWorkloadClass workloadClass)
    {
        lock (_gate)
        {
            var accumulator = GetOrAdd(operationId, workloadClass);
            accumulator.WorkloadClass = workloadClass;
            IncrementActive(workloadClass);
            if (workloadClass == OperationWorkloadClass.Historical)
            {
                _totalHistoricalOperations++;
            }
            else
            {
                _totalRealTimeOperations++;
            }
        }
    }

    public void RecordQueueWait(Guid operationId, TimeSpan queueWait)
    {
        lock (_gate)
        {
            var accumulator = GetOrAdd(operationId, OperationWorkloadClass.RealTime);
            accumulator.QueueWait = NormalizeDuration(queueWait);
        }
    }

    public void RecordChunking(Guid operationId, int chunkCount, TimeSpan duration)
    {
        lock (_gate)
        {
            var accumulator = GetOrAdd(operationId, OperationWorkloadClass.RealTime);
            accumulator.ChunkCount = Math.Max(0, chunkCount);
            accumulator.ChunkingDuration = NormalizeDuration(duration);
        }
    }

    public void RecordEmbedding(Guid operationId, int requestCount, TimeSpan duration)
    {
        lock (_gate)
        {
            var accumulator = GetOrAdd(operationId, OperationWorkloadClass.RealTime);
            accumulator.EmbeddingRequestCount = Math.Max(0, requestCount);
            accumulator.EmbeddingDuration = NormalizeDuration(duration);
        }
    }

    public void RecordIndexing(Guid operationId, TimeSpan duration)
    {
        lock (_gate)
        {
            var accumulator = GetOrAdd(operationId, OperationWorkloadClass.RealTime);
            accumulator.IndexingDuration = NormalizeDuration(duration);
        }
    }

    public void Complete(Guid operationId, OperationTerminalState terminalState, DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            if (!_inFlight.Remove(operationId, out var accumulator))
            {
                return;
            }

            DecrementActive(accumulator.WorkloadClass);
            _samples.Add(accumulator.ToSample(operationId, terminalState, completedAt));
        }
    }

    public void SetPendingCounts(int pendingRealTime, int pendingHistorical)
    {
        lock (_gate)
        {
            _pendingRealTime = Math.Max(0, pendingRealTime);
            _pendingHistorical = Math.Max(0, pendingHistorical);
        }
    }

    public HistoricalTelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            var samples = _samples.ToArray();
            var realTimeSamples = samples
                .Where(sample => sample.WorkloadClass == OperationWorkloadClass.RealTime)
                .ToArray();

            var totalQueueWait = TimeSpan.FromTicks(realTimeSamples.Sum(sample => sample.QueueWait.Ticks));
            var maximumQueueWait = realTimeSamples.Length == 0
                ? TimeSpan.Zero
                : realTimeSamples.Max(sample => sample.QueueWait);
            var averageQueueWait = realTimeSamples.Length == 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(totalQueueWait.Ticks / realTimeSamples.Length);

            return new HistoricalTelemetrySnapshot(
                samples,
                _totalRealTimeOperations,
                _totalHistoricalOperations,
                new OperationQueueGauge(_activeRealTime, _pendingRealTime, _activeHistorical, _pendingHistorical),
                new RealTimeQueueImpact(realTimeSamples.Length, totalQueueWait, maximumQueueWait, averageQueueWait));
        }
    }

    private OperationAccumulator GetOrAdd(Guid operationId, OperationWorkloadClass workloadClass)
    {
        if (!_inFlight.TryGetValue(operationId, out var accumulator))
        {
            accumulator = new OperationAccumulator(workloadClass);
            _inFlight.Add(operationId, accumulator);
        }

        return accumulator;
    }

    private void IncrementActive(OperationWorkloadClass workloadClass)
    {
        if (workloadClass == OperationWorkloadClass.Historical)
        {
            _activeHistorical++;
        }
        else
        {
            _activeRealTime++;
        }
    }

    private void DecrementActive(OperationWorkloadClass workloadClass)
    {
        if (workloadClass == OperationWorkloadClass.Historical)
        {
            if (_activeHistorical > 0)
            {
                _activeHistorical--;
            }
        }
        else if (_activeRealTime > 0)
        {
            _activeRealTime--;
        }
    }

    private static TimeSpan NormalizeDuration(TimeSpan duration) => duration < TimeSpan.Zero ? TimeSpan.Zero : duration;

    private sealed class OperationAccumulator(OperationWorkloadClass workloadClass)
    {
        public OperationWorkloadClass WorkloadClass { get; set; } = workloadClass;

        public TimeSpan QueueWait { get; set; }

        public int ChunkCount { get; set; }

        public TimeSpan ChunkingDuration { get; set; }

        public int EmbeddingRequestCount { get; set; }

        public TimeSpan EmbeddingDuration { get; set; }

        public TimeSpan IndexingDuration { get; set; }

        public OperationTelemetrySample ToSample(
            Guid operationId,
            OperationTerminalState terminalState,
            DateTimeOffset completedAt) => new(
            operationId,
            WorkloadClass,
            QueueWait,
            ChunkCount,
            ChunkingDuration,
            EmbeddingRequestCount,
            EmbeddingDuration,
            IndexingDuration,
            terminalState,
            completedAt);
    }
}
