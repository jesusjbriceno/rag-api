using System.Runtime.InteropServices;

namespace Rag.HistoricalLoader.Core.Benchmark;

/// <summary>
/// Operator-approved benchmark probe configuration. Defaults reflect the predeclared
/// sample parameters recorded in apply-progress before this unit started: a 30-candidate
/// budget, a minimum of one candidate per non-empty stratum, a 95% ±10% confidence/coverage
/// rule, and a two-hour sustained run.
/// </summary>
public sealed record ProbeOptions
{
    public Guid SampleSetId { get; init; }

    public int SampleBudget { get; init; } = 30;

    public int MinimumPerStratum { get; init; } = 1;

    public string ConfidenceCoverageRules { get; init; } = "95% confidence, ±10% margin";

    public TimeSpan SustainedDuration { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan TimeoutThreshold { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan HangThreshold { get; init; } = TimeSpan.FromMinutes(15);

    public double MaxErrorRate { get; init; } = 0.10;

    public double MaxTimeoutHangRate { get; init; } = 0.10;

    public string MeasurementMethod { get; init; } = "stopwatch";

    public string Machine { get; init; } = Environment.MachineName;

    public string OperatingSystem { get; init; } = RuntimeInformation.OSDescription;

    public string RuntimeVersion { get; init; } = Environment.Version.ToString();

    public string AdapterName { get; init; } = "reference-identity";

    public string AdapterVersion { get; init; } = "0";

    public string Configuration { get; init; } = "manifest-metadata reference; content not opened";
}
