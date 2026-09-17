using Rag.HistoricalLoader.Core.Benchmark;
using Rag.HistoricalLoader.Core.Configuration;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Engine;
using Rag.HistoricalLoader.Engine.Control;

return await HistoricalLoaderProgram.RunAsync(args);

internal static class HistoricalLoaderProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        return args[0] switch
        {
            "inventory" => await RunInventoryAsync(args),
            "select-sample" => await RunSelectSampleAsync(args),
            "benchmark" when args.Length >= 2 && args[1] == "extraction" => await RunBenchmarkExtractionAsync(args),
            "serve" => await ControlServeCommand.RunAsync(args),
            _ => Usage(),
        };
    }

    private static async Task<int> RunInventoryAsync(string[] args)
    {
        var options = new HistoricalLoaderOptions();
        var databasePath = options.DatabasePath;
        var outputDirectory = (string?)null;
        var maxBytes = options.MaxCandidateBytes;
        var roots = new List<SourceRoot>();

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--db" && i + 1 < args.Length) databasePath = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) outputDirectory = args[++i];
            else if (args[i] == "--max-bytes" && i + 1 < args.Length) maxBytes = long.Parse(args[++i]);
            else if (args[i] == "--root" && i + 1 < args.Length) roots.Add(Root(args[++i]));
        }

        if (outputDirectory is null || roots.Count == 0)
        {
            Console.Error.WriteLine("inventory requires --out and at least one --root.");
            return 2;
        }

        var engine = new HistoricalLoaderEngine();
        var result = await engine.RunInventoryAsync(new InventoryRunRequest(databasePath, outputDirectory, roots, maxBytes));
        Console.WriteLine(result.Export.IsComplete ? "inventory complete" : "inventory incomplete");
        return result.Export.IsComplete ? 0 : 1;
    }

    private static async Task<int> RunSelectSampleAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var manifestId))
        {
            Console.Error.WriteLine("select-sample requires a manifest id.");
            return 2;
        }

        var options = new HistoricalLoaderOptions();
        var databasePath = options.DatabasePath;
        var budget = 0;
        long seed = 0;
        var hasSeed = false;
        var minimumPerStratum = 1;
        var coverageRules = string.Empty;
        var sizeBands = 4;

        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--db" && i + 1 < args.Length) databasePath = args[++i];
            else if (args[i] == "--budget" && i + 1 < args.Length) budget = int.Parse(args[++i]);
            else if (args[i] == "--seed" && i + 1 < args.Length) { seed = long.Parse(args[++i]); hasSeed = true; }
            else if (args[i] == "--min-per-stratum" && i + 1 < args.Length) minimumPerStratum = int.Parse(args[++i]);
            else if (args[i] == "--coverage-rules" && i + 1 < args.Length) coverageRules = args[++i];
            else if (args[i] == "--size-bands" && i + 1 < args.Length) sizeBands = int.Parse(args[++i]);
        }

        if (budget <= 0 || !hasSeed)
        {
            Console.Error.WriteLine("select-sample requires --budget and --seed.");
            return 2;
        }

        var engine = new HistoricalLoaderEngine();
        var result = await engine.RunSelectSampleAsync(new SelectSampleRequest(databasePath, manifestId, budget, seed, minimumPerStratum, coverageRules, sizeBands));
        Console.WriteLine(result.Selection.GatePassed ? "sample selected" : "sample gate failed");
        Console.WriteLine($"sample_set_id: {result.Selection.SampleSet.Id}");
        return result.Selection.GatePassed ? 0 : 1;
    }

        private static async Task<int> RunBenchmarkExtractionAsync(string[] args)
        {
            if (args.Length < 3 || !Guid.TryParse(args[2], out var sampleSetId))
            {
                Console.Error.WriteLine("benchmark extraction requires a sample-set id.");
                return 2;
            }

            var options = new ProbeOptions();
            var databasePath = new HistoricalLoaderOptions().DatabasePath;
            string? companionAssemblyPath = null;
            for (var i = 3; i < args.Length; i++)
            {
                if (args[i] == "--db" && i + 1 < args.Length) databasePath = args[++i];
                else if (args[i] == "--companion" && i + 1 < args.Length) companionAssemblyPath = args[++i];
                else if (args[i] == "--adapter" && i + 1 < args.Length) options = options with { AdapterName = args[++i] };
                else if (args[i] == "--sustained" && i + 1 < args.Length) options = options with { SustainedDuration = TimeSpan.Parse(args[++i]) };
                else if (args[i] == "--sampling-interval" && i + 1 < args.Length) options = options with { SamplingInterval = TimeSpan.Parse(args[++i]) };
                else if (args[i] == "--budget" && i + 1 < args.Length) options = options with { SampleBudget = int.Parse(args[++i]) };
                else if (args[i] == "--min-per-stratum" && i + 1 < args.Length) options = options with { MinimumPerStratum = int.Parse(args[++i]) };
                else if (args[i] == "--confidence" && i + 1 < args.Length) options = options with { ConfidenceCoverageRules = args[++i] };
                else if (args[i] == "--max-error-rate" && i + 1 < args.Length) options = options with { MaxErrorRate = double.Parse(args[++i]) };
                else if (args[i] == "--max-timeout-hang-rate" && i + 1 < args.Length) options = options with { MaxTimeoutHangRate = double.Parse(args[++i]) };
            }

            var engine = new HistoricalLoaderEngine();
            var result = await engine.RunBenchmarkExtractionAsync(new BenchmarkExtractionRequest(databasePath, sampleSetId, options, companionAssemblyPath));
            Console.WriteLine($"benchmark complete: {result.ObservationCount} observations; count={result.Report.Count}, errors={result.Report.Errors}, timeouts={result.Report.Timeouts}, hangs={result.Report.Hangs}, sustained_satisfied={result.Report.SustainedReliabilitySatisfied}");
            return result.Report.SustainedReliabilitySatisfied ? 0 : 1;
        }

        private static SourceRoot Root(string value)
    {
        var separator = value.IndexOf('=');
        return separator < 0
            ? new SourceRoot(Guid.NewGuid(), value, value, DateTimeOffset.UtcNow)
            : new SourceRoot(Guid.NewGuid(), value[..separator], value[(separator + 1)..], DateTimeOffset.UtcNow);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: Rag.HistoricalLoader.Engine <inventory|select-sample|benchmark|serve> ...");
        Console.Error.WriteLine("  inventory --db <path> --out <dir> --root <label=path> [--max-bytes <n>]");
        Console.Error.WriteLine("  select-sample <manifest-id> --db <path> --budget <n> --seed <s> [--min-per-stratum <n>] [--coverage-rules <json>] [--size-bands <n>]");
        Console.Error.WriteLine("  benchmark extraction <sample-set-id> --db <path> [--companion <assembly-path>] [--adapter <name>] [--sustained <hh:mm:ss>] [--sampling-interval <hh:mm:ss>] [--budget <n>] [--min-per-stratum <n>] [--confidence <json>] [--max-error-rate <r>] [--max-timeout-hang-rate <r>]");
        Console.Error.WriteLine(ControlServeCommand.UsageLine);
        return 2;
    }
}
