using System.Reflection;
using Rag.Application;
using Rag.Domain;

namespace Rag.UnitTests;

public sealed class OperationTelemetryTests
{
    [Fact]
    public void Records_a_complete_operation_sample_with_all_timing_fields()
    {
        var telemetry = new HistoricalTelemetry();
        var operationId = Guid.NewGuid();
        var completedAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

        telemetry.Begin(operationId, OperationWorkloadClass.RealTime);
        telemetry.RecordQueueWait(operationId, TimeSpan.FromMilliseconds(12));
        telemetry.RecordChunking(operationId, 3, TimeSpan.FromMilliseconds(4));
        telemetry.RecordEmbedding(operationId, 1, TimeSpan.FromMilliseconds(40));
        telemetry.RecordIndexing(operationId, TimeSpan.FromMilliseconds(6));
        telemetry.Complete(operationId, OperationTerminalState.Succeeded, completedAt);

        var sample = Assert.Single(telemetry.Snapshot().Samples);
        Assert.Equal(operationId, sample.OperationId);
        Assert.Equal(OperationWorkloadClass.RealTime, sample.WorkloadClass);
        Assert.Equal(TimeSpan.FromMilliseconds(12), sample.QueueWait);
        Assert.Equal(3, sample.ChunkCount);
        Assert.Equal(TimeSpan.FromMilliseconds(4), sample.ChunkingDuration);
        Assert.Equal(1, sample.EmbeddingRequestCount);
        Assert.Equal(TimeSpan.FromMilliseconds(40), sample.EmbeddingDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(6), sample.IndexingDuration);
        Assert.Equal(OperationTerminalState.Succeeded, sample.TerminalState);
        Assert.Equal(completedAt, sample.CompletedAt);
    }

    [Fact]
    public void Snapshot_computes_real_time_queue_impact_and_workload_totals()
    {
        var telemetry = new HistoricalTelemetry();
        RecordSample(telemetry, OperationWorkloadClass.RealTime, TimeSpan.FromMilliseconds(10));
        RecordSample(telemetry, OperationWorkloadClass.RealTime, TimeSpan.FromMilliseconds(20));
        RecordSample(telemetry, OperationWorkloadClass.Historical, TimeSpan.FromMilliseconds(999));

        var snapshot = telemetry.Snapshot();

        Assert.Equal(3, snapshot.Samples.Count);
        Assert.Equal(2, snapshot.TotalRealTimeOperations);
        Assert.Equal(1, snapshot.TotalHistoricalOperations);
        Assert.Equal(2, snapshot.RealTimeQueueImpact.SampleCount);
        Assert.Equal(TimeSpan.FromMilliseconds(30), snapshot.RealTimeQueueImpact.TotalQueueWait);
        Assert.Equal(TimeSpan.FromMilliseconds(20), snapshot.RealTimeQueueImpact.MaximumQueueWait);
        Assert.Equal(TimeSpan.FromMilliseconds(15), snapshot.RealTimeQueueImpact.AverageQueueWait);
    }

    [Fact]
    public void Tracks_active_and_pending_counts_in_the_queue_gauge()
    {
        var telemetry = new HistoricalTelemetry();
        telemetry.SetPendingCounts(7, 3);
        var operationId = Guid.NewGuid();

        telemetry.Begin(operationId, OperationWorkloadClass.Historical);
        var inFlight = telemetry.Snapshot();
        Assert.Equal(1, inFlight.QueueGauge.ActiveHistorical);
        Assert.Equal(0, inFlight.QueueGauge.ActiveRealTime);
        Assert.Equal(7, inFlight.QueueGauge.PendingRealTime);
        Assert.Equal(3, inFlight.QueueGauge.PendingHistorical);

        telemetry.Complete(operationId, OperationTerminalState.Failed, DateTimeOffset.UtcNow);
        var afterCompletion = telemetry.Snapshot();
        Assert.Equal(0, afterCompletion.QueueGauge.ActiveHistorical);
    }

    [Fact]
    public void Snapshot_is_immutable_and_bounded_to_memory()
    {
        var telemetry = new HistoricalTelemetry();
        RecordSample(telemetry, OperationWorkloadClass.RealTime, TimeSpan.Zero);

        var earlier = telemetry.Snapshot();
        Assert.Single(earlier.Samples);

        RecordSample(telemetry, OperationWorkloadClass.RealTime, TimeSpan.Zero);

        Assert.Single(earlier.Samples);
        Assert.Equal(2, telemetry.Snapshot().Samples.Count);
    }

    [Fact]
    public void Default_classifier_treats_operations_as_real_time()
    {
        IOperationWorkloadClassifier classifier = new DefaultOperationWorkloadClassifier();
        var operation = Operation.CreatePending(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(OperationWorkloadClass.RealTime, classifier.Classify(operation));
    }

    [Fact]
    public void Telemetry_recorder_exposes_no_persistence_surface()
    {
        var methods = typeof(HistoricalTelemetry)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Begin",
                "Complete",
                "RecordChunking",
                "RecordEmbedding",
                "RecordIndexing",
                "RecordQueueWait",
                "SetPendingCounts",
                "Snapshot",
            },
            methods);
    }

    [Fact]
    public void Telemetry_types_are_not_exposed_by_the_application_contract()
    {
        var telemetryTypes = new[]
        {
            typeof(HistoricalTelemetry),
            typeof(OperationTelemetrySample),
            typeof(HistoricalTelemetrySnapshot),
            typeof(OperationQueueGauge),
            typeof(RealTimeQueueImpact),
            typeof(IOperationWorkloadClassifier),
            typeof(OperationWorkloadClass),
            typeof(OperationTerminalState),
        };

        var application = typeof(IOperationProcessor).Assembly;
        foreach (var type in application.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain(ExposedTypes(member), type => telemetryTypes.Contains(type));
            }
        }
    }

    private static void RecordSample(
        HistoricalTelemetry telemetry,
        OperationWorkloadClass workloadClass,
        TimeSpan queueWait)
    {
        var operationId = Guid.NewGuid();
        telemetry.Begin(operationId, workloadClass);
        telemetry.RecordQueueWait(operationId, queueWait);
        telemetry.Complete(operationId, OperationTerminalState.Succeeded, DateTimeOffset.UtcNow);
    }

    private static IEnumerable<Type> ExposedTypes(MemberInfo member)
    {
        switch (member)
        {
            case MethodBase method:
                yield return method is MethodInfo info ? info.ReturnType : typeof(void);
                foreach (var parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }

                yield break;
            case PropertyInfo property:
                yield return property.PropertyType;
                yield break;
            case FieldInfo field:
                yield return field.FieldType;
                yield break;
            case EventInfo @event when @event.EventHandlerType is not null:
                yield return @event.EventHandlerType;
                yield break;
        }
    }
}
