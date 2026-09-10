namespace Rag.HistoricalLoader.Core.Classification;

public static class EligibilityCodes
{
    public const string Eligible = "eligible";
    public const string UnsupportedFormat = "unsupported_format";
    public const string SizePolicyExceeded = "size_policy_exceeded";
    public const string AccessDenied = "access_denied";
    public const string ReparsePoint = "reparse_point";
}

public sealed record CandidateDiscovery(string RelativePath, long ByteSize, bool IsReparsePoint, string? AccessErrorCode);

public sealed record ClassificationResult(string EligibilityCode, string? DiscoveryErrorCode = null);

public sealed class CandidateClassifier
{
    private static readonly string[] SupportedExtensions = [".doc", ".docx", ".md", ".pdf", ".txt"];

    private readonly long _maxByteSize;

    public CandidateClassifier(long maxByteSize)
    {
        if (maxByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxByteSize), "The maximum candidate byte size must be positive.");
        }

        _maxByteSize = maxByteSize;
    }

    public ClassificationResult Classify(CandidateDiscovery discovery)
    {
        if (discovery.IsReparsePoint)
        {
            return new ClassificationResult(EligibilityCodes.ReparsePoint);
        }

        if (discovery.AccessErrorCode is not null)
        {
            return new ClassificationResult(EligibilityCodes.AccessDenied, discovery.AccessErrorCode);
        }

        var extension = Path.GetExtension(discovery.RelativePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new ClassificationResult(EligibilityCodes.UnsupportedFormat, extension);
        }

        if (discovery.ByteSize > _maxByteSize)
        {
            return new ClassificationResult(EligibilityCodes.SizePolicyExceeded);
        }

        return new ClassificationResult(EligibilityCodes.Eligible);
    }
}
