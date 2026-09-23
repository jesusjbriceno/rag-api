using System.Globalization;
using System.Text.RegularExpressions;

namespace Rag.HistoricalLoader.Core.Benchmark;

/// <summary>
/// Pure aggregation of probe observations into a benchmark report. Never claims full-corpus
/// feasibility; any weighted full-corpus range is an estimate with explicit assumptions.
/// </summary>
public static class BenchmarkReportBuilder
{
    public const int P95MinimumSample = 20;

    public static BenchmarkReport Build(
        ProbeRun run,
        ProbeOptions options,
        IReadOnlyDictionary<string, int>? stratumPopulations = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var observations = run.Observations;
        var count = observations.Count;
        var durations = observations.Select(o => o.ExtractionDuration).OrderBy(t => t).ToList();

        var median = count == 0 ? TimeSpan.Zero : Median(durations);
        TimeSpan? p95 = count >= P95MinimumSample ? Percentile(durations, 0.95) : null;
        var min = count == 0 ? TimeSpan.Zero : durations[0];
        var max = count == 0 ? TimeSpan.Zero : durations[^1];

        var errors = observations.Count(o => o.Outcome == BenchmarkOutcome.Error);
        var timeouts = observations.Count(o => o.Outcome == BenchmarkOutcome.Timeout);
        var hangs = observations.Count(o => o.Outcome == BenchmarkOutcome.Hang);
        var errorRate = count == 0 ? 0 : (double)errors / count;
        var timeoutHangRate = count == 0 ? 0 : (double)(timeouts + hangs) / count;

        var hours = Math.Max(run.Elapsed.TotalHours, double.Epsilon);
        var documentsPerHour = count / hours;
        var sourceGbPerHour = observations.Sum(o => o.SourceBytes) / 1_000_000_000.0 / hours;
        var normalizedGbPerHour = observations.Sum(o => o.NormalizedTextBytes) / 1_000_000_000.0 / hours;

        var peakResource = count == 0
            ? new ResourceSnapshot(0, 0, 0, 0, TimeSpan.Zero)
            : new ResourceSnapshot(
                observations.Max(o => o.Resource.CpuPercent),
                observations.Max(o => o.Resource.WorkingSetBytes),
                observations.Max(o => o.Resource.DiskIoBytes),
                0,
                TimeSpan.Zero);
        var peakQueueGrowth = count == 0 ? 0 : observations.Max(o => o.Resource.QueueGrowth);

        var (satisfied, reason) = EvaluateReliability(run, options, errorRate, timeoutHangRate);
        var fullCorpusRange = EstimateFullCorpusRange(observations, stratumPopulations, options, satisfied);
        var limitations = BuildLimitations(count, p95, stratumPopulations, observations);

        return new BenchmarkReport(
            options.SampleSetId,
            new EnvironmentFingerprint(
                options.MeasurementMethod,
                options.SamplingInterval,
                options.Machine,
                options.OperatingSystem,
                options.RuntimeVersion,
                options.AdapterName,
                options.AdapterVersion,
                options.Configuration),
            count,
            median,
            p95,
            min,
            max,
            documentsPerHour,
            sourceGbPerHour,
            normalizedGbPerHour,
            errors,
            timeouts,
            hangs,
            errorRate,
            timeoutHangRate,
            peakResource,
            peakQueueGrowth,
            satisfied,
            reason,
            fullCorpusRange,
            limitations);
    }

    public static TimeSpan Median(IReadOnlyList<TimeSpan> sorted)
    {
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2);
    }

    public static TimeSpan Percentile(IReadOnlyList<TimeSpan> sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static (bool Satisfied, string? Reason) EvaluateReliability(
        ProbeRun run,
        ProbeOptions options,
        double errorRate,
        double timeoutHangRate)
    {
        var reasons = new List<string>();
        if (run.Elapsed < options.SustainedDuration)
        {
            reasons.Add($"Run elapsed {run.Elapsed} is below the predeclared sustained duration {options.SustainedDuration}.");
        }

        if (errorRate > options.MaxErrorRate)
        {
            reasons.Add($"Error rate {errorRate:P1} exceeds the predeclared maximum {options.MaxErrorRate:P1}.");
        }

        if (timeoutHangRate > options.MaxTimeoutHangRate)
        {
            reasons.Add($"Timeout/hang rate {timeoutHangRate:P1} exceeds the predeclared maximum {options.MaxTimeoutHangRate:P1}.");
        }

        return reasons.Count == 0 ? (true, null) : (false, string.Join(" ", reasons));
    }

    private static IReadOnlyList<string> BuildLimitations(
        int count,
        TimeSpan? p95,
        IReadOnlyDictionary<string, int>? stratumPopulations,
        IReadOnlyList<BenchmarkObservation> observations)
    {
        var limitations = new List<string>
        {
            "Full-corpus feasibility is not claimed by this probe; any weighted range is an estimate with assumptions.",
        };

        if (p95 is null && count > 0)
        {
            limitations.Add($"p95 not reported (sample size {count} is below {P95MinimumSample}).");
        }

        if (stratumPopulations is null || stratumPopulations.Count == 0)
        {
            limitations.Add("Full-corpus weighted range not estimated: no stratum population data supplied.");
        }
        else if (observations.Any(o => !stratumPopulations.ContainsKey(o.StratumKey)))
        {
            limitations.Add("Full-corpus weighted range not estimated: uncovered strata (missing population).");
        }

        return limitations;
    }

    private static WeightedFullCorpusRange? EstimateFullCorpusRange(
        IReadOnlyList<BenchmarkObservation> observations,
        IReadOnlyDictionary<string, int>? stratumPopulations,
        ProbeOptions options,
        bool satisfied)
    {
        if (stratumPopulations is null || stratumPopulations.Count == 0 || observations.Count == 0)
        {
            return null;
        }

        if (observations.Any(o => !stratumPopulations.ContainsKey(o.StratumKey)))
        {
            return null;
        }

        var margin = ParseMargin(options.ConfidenceCoverageRules);
        var totalMilliseconds = observations
            .GroupBy(o => o.StratumKey, StringComparer.Ordinal)
            .Sum(group =>
            {
                var meanTotal = group.Average(o =>
                    (o.DiscoveryDuration + o.SnapshotDuration + o.ExtractionDuration + o.StagingDuration).TotalMilliseconds);
                return stratumPopulations[group.Key] * meanTotal;
            });

        var lower = TimeSpan.FromMilliseconds(totalMilliseconds * (1 - margin));
        var upper = TimeSpan.FromMilliseconds(totalMilliseconds * (1 + margin));
        var assumptions = new List<string>
        {
            "Linear scaling from observed sample strata; no hidden document complexity is modeled.",
            $"Margin ±{margin:P0} from operator confidence/coverage rules.",
            "Full-corpus feasibility is not claimed by this estimate.",
            satisfied
                ? "Sustained reliability criteria were satisfied at estimation time."
                : "Sustained reliability criteria were NOT satisfied; the estimate is provisional.",
        };

        return new WeightedFullCorpusRange(lower, upper, assumptions);
    }

    private static double ParseMargin(string rules)
    {
        if (string.IsNullOrWhiteSpace(rules))
        {
            return 0;
        }

        var match = Regex.Match(rules, @"±\s*(\d+(?:\.\d+)?)\s*%");
        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin)
                ? margin / 100
                : 0;
    }
}
