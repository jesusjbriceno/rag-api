namespace Rag.HistoricalLoader.Core.Configuration;

public sealed class HistoricalLoaderOptions
{
    public const string SectionName = "HistoricalLoader";

    public string DatabasePath { get; set; } = "historical-loader.sqlite";

    public long MaxCandidateBytes { get; set; } = 10L * 1024 * 1024;
}
