using Rag.HistoricalLoader.Core.Benchmark;

namespace Rag.HistoricalLoader.UnitTests.Benchmark;

public sealed class ExtractionProbeTests
{
    private static readonly Guid SampleSetId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task RunAsync_MeasuresPerCandidatePhaseDurationsAndResources()
    {
        var clock = new ManualClock();
        var options = Options() with { SustainedDuration = TimeSpan.FromMilliseconds(1) };
        var probe = new ExtractionProbe(options, clock);
        var candidate = new BenchmarkCandidate(Guid.NewGuid(), ".docx|1", 500);

        var run = await probe.RunAsync(
            [candidate],
            Phase(clock, 0, "completed", TimeSpan.FromMilliseconds(10)),
            Phase(clock, 500, "completed", TimeSpan.FromMilliseconds(20)),
            Phase(clock, 300, "completed", TimeSpan.FromMilliseconds(30)),
            Phase(clock, 0, "completed", TimeSpan.FromMilliseconds(40)),
            elapsed => new ResourceSnapshot(1, 2, 3, 0, elapsed));

        var observation = Assert.Single(run.Observations);
        Assert.Equal(TimeSpan.FromMilliseconds(10), observation.DiscoveryDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(20), observation.SnapshotDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(30), observation.ExtractionDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(40), observation.StagingDuration);
        Assert.Equal(500, observation.SourceBytes);
        Assert.Equal(300, observation.NormalizedTextBytes);
        Assert.Equal(BenchmarkOutcome.Completed, observation.Outcome);
        Assert.Equal(1, observation.Resource.CpuPercent);
        Assert.Equal(2, observation.Resource.WorkingSetBytes);
        Assert.Equal(3, observation.Resource.DiskIoBytes);
    }

    [Fact]
    public void Report_RecordsEnvironmentFingerprint()
    {
        var options = Options() with
        {
            SamplingInterval = TimeSpan.FromSeconds(5),
            MeasurementMethod = "stopwatch",
            Machine = "TEST-MACHINE",
            OperatingSystem = "TestOS",
            RuntimeVersion = "10.0.0",
            AdapterName = "libreoffice-probe",
            AdapterVersion = "7.6",
            Configuration = "cold",
        };
        var observations = new[] { Observation(0, TimeSpan.FromSeconds(1), 100, 50) };

        var report = BenchmarkReportBuilder.Build(new ProbeRun(observations, TimeSpan.FromHours(1)), options);

        Assert.Equal("stopwatch", report.Environment.MeasurementMethod);
        Assert.Equal(TimeSpan.FromSeconds(5), report.Environment.SamplingInterval);
        Assert.Equal("TEST-MACHINE", report.Environment.Machine);
        Assert.Equal("TestOS", report.Environment.OperatingSystem);
        Assert.Equal("10.0.0", report.Environment.RuntimeVersion);
        Assert.Equal("libreoffice-probe", report.Environment.AdapterName);
        Assert.Equal("7.6", report.Environment.AdapterVersion);
        Assert.Equal("cold", report.Environment.Configuration);
    }

    [Fact]
    public void ClassifiesLibreOfficeTimeoutAndHangExplicitly()
    {
        var options = Options() with { TimeoutThreshold = TimeSpan.FromMinutes(5), HangThreshold = TimeSpan.FromMinutes(15) };

        Assert.Equal(BenchmarkOutcome.Timeout, ExtractionProbe.ClassifyExtractionOutcome("timeout", TimeSpan.FromSeconds(1), options));
        Assert.Equal(BenchmarkOutcome.Hang, ExtractionProbe.ClassifyExtractionOutcome("hang", TimeSpan.FromSeconds(1), options));
        Assert.Equal(BenchmarkOutcome.Timeout, ExtractionProbe.ClassifyExtractionOutcome("completed", TimeSpan.FromMinutes(6), options));
        Assert.Equal(BenchmarkOutcome.Hang, ExtractionProbe.ClassifyExtractionOutcome("completed", TimeSpan.FromMinutes(16), options));
        Assert.Equal(BenchmarkOutcome.Completed, ExtractionProbe.ClassifyExtractionOutcome("completed", TimeSpan.FromSeconds(1), options));
        Assert.Equal(BenchmarkOutcome.Error, ExtractionProbe.ClassifyExtractionOutcome("error", TimeSpan.FromSeconds(1), options));
    }

    [Fact]
    public async Task RunAsync_ReportsRatesAndRefusesFeasibilityWhenErrorRateExceedsThreshold()
    {
        var clock = new ManualClock();
        var options = Options() with
        {
            SustainedDuration = TimeSpan.FromMilliseconds(10),
            MaxErrorRate = 0.10,
            MaxTimeoutHangRate = 0.10,
        };
        var probe = new ExtractionProbe(options, clock);
        var errorId = Guid.NewGuid();
        var candidates = new[]
        {
            new BenchmarkCandidate(errorId, ".docx|1", 100),
            new BenchmarkCandidate(Guid.NewGuid(), ".docx|1", 100),
            new BenchmarkCandidate(Guid.NewGuid(), ".pdf|1", 100),
            new BenchmarkCandidate(Guid.NewGuid(), ".pdf|1", 100),
        };

        CandidatePhase step = (c, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return Task.FromResult(new PhaseResult(100, "completed", null));
        };
        CandidatePhase extract = (c, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var failed = c.CandidateId == errorId;
            return Task.FromResult(new PhaseResult(100, failed ? "error" : "completed", failed ? "extract_failed" : null));
        };

        var run = await probe.RunAsync(candidates, step, step, extract, step, _ => new ResourceSnapshot(1, 1, 1, 0, clock.Elapsed));
        var report = BenchmarkReportBuilder.Build(run, options);

        Assert.Equal(4, report.Count);
        Assert.Equal(1, report.Errors);
        Assert.InRange(report.ErrorRate, 0.24, 0.26);
        Assert.False(report.SustainedReliabilitySatisfied);
        Assert.Contains("error rate", report.ReliabilityBlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_SustainedRunSatisfiedWhenCriteriaMet()
    {
        var clock = new ManualClock();
        var options = Options() with { SustainedDuration = TimeSpan.FromMilliseconds(10) };
        var probe = new ExtractionProbe(options, clock);
        var candidates = Enumerable.Range(0, 4).Select(_ => new BenchmarkCandidate(Guid.NewGuid(), ".docx|1", 100)).ToArray();
        CandidatePhase step = (c, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return Task.FromResult(new PhaseResult(100, "completed", null));
        };

        var run = await probe.RunAsync(candidates, step, step, step, step, _ => new ResourceSnapshot(1, 1, 1, 0, clock.Elapsed));
        var report = BenchmarkReportBuilder.Build(run, options);

        Assert.True(report.SustainedReliabilitySatisfied);
        Assert.Null(report.ReliabilityBlockReason);
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.Timeouts);
        Assert.Equal(0, report.Hangs);
    }

    [Fact]
    public async Task RunAsync_RefusesWhenRunDoesNotReachPredeclaredDuration()
    {
        var clock = new ManualClock();
        var options = Options() with { SustainedDuration = TimeSpan.FromHours(2) };
        var probe = new ExtractionProbe(options, clock);
        CandidatePhase noop = (c, _) => Task.FromResult(new PhaseResult(0, "completed", null));

        var run = await probe.RunAsync([], noop, noop, noop, noop, _ => new ResourceSnapshot(0, 0, 0, 0, TimeSpan.Zero));
        var report = BenchmarkReportBuilder.Build(run, options);

        Assert.False(report.SustainedReliabilitySatisfied);
        Assert.Contains("duration", report.ReliabilityBlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_ComposesCountMedianP95RangeAndThroughput()
    {
        var observations = Enumerable.Range(0, 30)
            .Select(i => Observation(i, TimeSpan.FromMilliseconds(100 * (i + 1)), 1_000_000, 500_000))
            .ToArray();

        var report = BenchmarkReportBuilder.Build(new ProbeRun(observations, TimeSpan.FromHours(1)), Options());

        Assert.Equal(30, report.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1550), report.MedianExtraction);
        Assert.Equal(TimeSpan.FromMilliseconds(2900), report.P95Extraction);
        Assert.Equal(TimeSpan.FromMilliseconds(100), report.MinExtraction);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), report.MaxExtraction);
        Assert.InRange(report.DocumentsPerHour, 29.9, 30.1);
        Assert.InRange(report.SourceGbPerHour, 0.0299, 0.0301);
        Assert.InRange(report.NormalizedGbPerHour, 0.0149, 0.0151);
    }

    [Fact]
    public void Report_OmitsP95WhenSampleSizeIsInsufficient()
    {
        var observations = Enumerable.Range(0, 5)
            .Select(i => Observation(i, TimeSpan.FromMilliseconds(100 * (i + 1)), 1000, 500))
            .ToArray();

        var report = BenchmarkReportBuilder.Build(new ProbeRun(observations, TimeSpan.FromHours(1)), Options());

        Assert.Null(report.P95Extraction);
        Assert.Contains(report.Limitations, l => l.Contains("p95", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Report_EstimatesWeightedFullCorpusRangeWithAssumptionsButDoesNotClaimFeasibility()
    {
        var observations = new[]
        {
            Observation(0, TimeSpan.FromSeconds(1), 1000, 500, stratum: ".docx|1"),
            Observation(1, TimeSpan.FromSeconds(2), 1000, 500, stratum: ".docx|1"),
            Observation(2, TimeSpan.FromSeconds(2), 1000, 500, stratum: ".pdf|1"),
            Observation(3, TimeSpan.FromSeconds(4), 1000, 500, stratum: ".pdf|1"),
        };
        var populations = new Dictionary<string, int> { [".docx|1"] = 100, [".pdf|1"] = 50 };

        var report = BenchmarkReportBuilder.Build(new ProbeRun(observations, TimeSpan.FromHours(1)), Options(), populations);

        Assert.NotNull(report.FullCorpusRange);
        Assert.Equal(TimeSpan.FromMilliseconds(270000), report.FullCorpusRange!.LowerBound);
        Assert.Equal(TimeSpan.FromMilliseconds(330000), report.FullCorpusRange.UpperBound);
        Assert.NotEmpty(report.FullCorpusRange.Assumptions);
        Assert.Contains(report.Limitations, l => l.Contains("not claimed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProbeOptions_DefaultsMatchApprovedParameters()
    {
        var options = new ProbeOptions();

        Assert.Equal(30, options.SampleBudget);
        Assert.Equal(1, options.MinimumPerStratum);
        Assert.Contains("95%", options.ConfidenceCoverageRules);
        Assert.Contains("10%", options.ConfidenceCoverageRules);
        Assert.Equal(TimeSpan.FromHours(2), options.SustainedDuration);
    }

    private static ProbeOptions Options() => new() { SampleSetId = SampleSetId };

    private static CandidatePhase Phase(ManualClock clock, long bytes, string outcome, TimeSpan advance)
        => (_, _) =>
        {
            clock.Advance(advance);
            return Task.FromResult(new PhaseResult(bytes, outcome, null));
        };

    private static BenchmarkObservation Observation(
        int i,
        TimeSpan extraction,
        long sourceBytes,
        long normalizedBytes,
        BenchmarkOutcome outcome = BenchmarkOutcome.Completed,
        string stratum = ".docx|1")
        => new(
            Guid.NewGuid(),
            SampleSetId,
            Guid.NewGuid(),
            stratum,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            TimeSpan.Zero,
            extraction,
            TimeSpan.Zero,
            sourceBytes,
            normalizedBytes,
            outcome,
            outcome == BenchmarkOutcome.Completed ? null : "fault",
            new ResourceSnapshot(10 + i, 1000 + i, 2000 + i, i, TimeSpan.Zero));

    private sealed class ManualClock : IBenchmarkClock
    {
        public TimeSpan Elapsed { get; private set; }

        public void Advance(TimeSpan delta) => Elapsed += delta;
    }
}
