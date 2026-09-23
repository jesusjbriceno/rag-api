namespace Rag.HistoricalLoader.Core.Benchmark;

/// <summary>
/// Real-extraction probe phase. Distinct from the synthetic reference-identity path: it invokes a
/// local <see cref="ILocalTextExtractor"/> on the resolved source path and reports the normalized
/// byte count without ever emitting document content.
/// </summary>
public static class RealExtractionPhases
{
    public static CandidatePhase Extract(ILocalTextExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);

        return async (candidate, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(candidate.SourcePath))
            {
                return new PhaseResult(0, "error", LocalExtractionErrorCodes.SourcePathUnavailable);
            }

            var result = await extractor.ExtractAsync(candidate.SourcePath, cancellationToken).ConfigureAwait(false);
            return new PhaseResult(result.NormalizedTextBytes, result.Outcome, result.ErrorCode);
        };
    }
}
