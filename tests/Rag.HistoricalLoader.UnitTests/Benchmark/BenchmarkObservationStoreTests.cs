using Rag.HistoricalLoader.Core.Benchmark;

namespace Rag.HistoricalLoader.UnitTests.Benchmark;

public sealed class BenchmarkObservationStoreTests
{
    private static readonly Guid SampleSetId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public async Task SaveReportRoundTripsAllRequiredFields()
    {
        var directory = NewDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "benchmark.sqlite");
            var options = new ProbeOptions() { SampleSetId = SampleSetId, SustainedDuration = TimeSpan.FromMilliseconds(5) };
            var clock = new ManualClock();
            var probe = new ExtractionProbe(options, clock);
            var candidate = new BenchmarkCandidate(Guid.NewGuid(), ".docx|1", 1000);
            CandidatePhase step = (c, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                return Task.FromResult(new PhaseResult(1000, "completed", null));
            };

            var run = await probe.RunAsync(
                [candidate],
                step,
                step,
                step,
                step,
                _ => new ResourceSnapshot(12.5, 4096, 2048, 1, clock.Elapsed));
            var report = BenchmarkReportBuilder.Build(run, options);

            await using (var store = new BenchmarkObservationStore(databasePath))
            {
                await store.InitializeAsync();
                foreach (var observation in run.Observations)
                {
                    await store.SaveObservationAsync(observation);
                }

                await store.SaveReportAsync(report);

                Assert.Equal(run.Observations.Count, await store.CountObservationsAsync(SampleSetId));
                var loaded = await store.LoadReportAsync(SampleSetId);

                Assert.NotNull(loaded);
                Assert.Equal(report.Count, loaded.Count);
                Assert.Equal(report.Errors, loaded.Errors);
                Assert.Equal(report.Timeouts, loaded.Timeouts);
                Assert.Equal(report.Hangs, loaded.Hangs);
                Assert.Equal(report.SustainedReliabilitySatisfied, loaded.SustainedReliabilitySatisfied);
                Assert.Equal(report.Environment.AdapterName, loaded.Environment.AdapterName);
                Assert.Equal(report.Environment.AdapterVersion, loaded.Environment.AdapterVersion);
                Assert.Equal(report.Environment.Configuration, loaded.Environment.Configuration);
                Assert.Equal(report.PeakResource.CpuPercent, loaded.PeakResource.CpuPercent);
                Assert.Equal(report.MedianExtraction, loaded.MedianExtraction);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rag-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed class ManualClock : IBenchmarkClock
    {
        public TimeSpan Elapsed { get; private set; }

        public void Advance(TimeSpan delta) => Elapsed += delta;
    }
}
