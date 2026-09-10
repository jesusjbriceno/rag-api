using System.Security.Cryptography;
using System.Text;
using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Sampling;

namespace Rag.HistoricalLoader.UnitTests.Sampling;

public sealed class SampleGoldenTests
{
    private static readonly Guid ManifestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RootId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Pinned after the first GREEN run; byte-stability is what the constant guards.
    private const string GoldenHash = "a466aaed78da607035e12aa7439b72005d94db91dc159c3937c5dc0d66ccdea3";

    [Fact]
    public void FixedManifestAndSeed_ProduceByteStableSelection()
    {
        var selector = new StratifiedSampleSelector();

        var first = selector.Select(Request(), FixedCandidates());
        var second = selector.Select(Request(), FixedCandidates());

        Assert.Equal(Canonical(first), Canonical(second));
        Assert.Equal(GoldenHash, Sha256(Canonical(first)));
    }

    [Fact]
    public void DifferentSeed_ChangesSelectionDeterministically()
    {
        var selector = new StratifiedSampleSelector();

        var a = selector.Select(Request(seed: 7), FixedCandidates());
        var b = selector.Select(Request(seed: 8), FixedCandidates());

        Assert.NotEqual(Sha256(Canonical(a)), Sha256(Canonical(b)));
    }

    [Fact]
    public void ChangedManifest_ChangesSelectionDeterministically()
    {
        var baseCandidates = FixedCandidates();
        var changed = baseCandidates.Concat(new[] { Candidate(999, ".docx", 999_999) }).ToArray();
        var selector = new StratifiedSampleSelector();

        var a = selector.Select(Request(), baseCandidates);
        var b = selector.Select(Request(), changed);

        Assert.NotEqual(Sha256(Canonical(a)), Sha256(Canonical(b)));
    }

    private static SampleSelectionRequest Request(long seed = 12345)
        => new(ManifestId, ManifestState.Complete, 8, seed, 1, "splitmix64", "{\"confidence\":0.95}", 2);

    private static Candidate[] FixedCandidates() =>
    [
        Candidate(1, ".docx", 100), Candidate(2, ".docx", 200),
        Candidate(3, ".pdf", 300), Candidate(4, ".pdf", 400),
        Candidate(5, ".docx", 1000), Candidate(6, ".docx", 2000),
        Candidate(7, ".pdf", 3000), Candidate(8, ".pdf", 4000),
        Candidate(9, ".txt", 150), Candidate(10, ".md", 250),
    ];

    private static Candidate Candidate(int seq, string ext, long size)
        => new(
            Guid.Parse($"00000000-0000-0000-0000-{seq:D12}"),
            ManifestId,
            RootId,
            $"f{seq}{ext}",
            ext,
            size,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seq),
            EligibilityCodes.Eligible);

    private static string Canonical(SampleSelectionResult result)
    {
        var builder = new StringBuilder();
        builder.Append(result.SampleSet.ManifestId.ToString("N")).Append('|')
            .Append(result.SampleSet.Seed).Append('|')
            .Append(result.SampleSet.Budget).Append('|')
            .Append(result.SampleSet.MinimumPerStratum).Append('|')
            .Append(result.GatePassed ? '1' : '0').Append('\n');
        foreach (var member in result.Members.OrderBy(m => m.SelectedRank))
        {
            builder.Append(member.CandidateId.ToString("N")).Append(':').Append(member.MetadataFingerprint).Append('\n');
        }

        foreach (var coverage in result.Coverage.OrderBy(c => c.StratumKey, StringComparer.Ordinal))
        {
            builder.Append(coverage.StratumKey).Append(':').Append(coverage.Population).Append('/').Append(coverage.Allocation).Append('\n');
        }

        foreach (var band in result.SizeBands.OrderBy(b => b.Index))
        {
            builder.Append("band").Append(band.Index).Append(':').Append(band.MinBytes).Append('-').Append(band.MaxBytes).Append(':').Append(band.Count).Append('\n');
        }

        return builder.ToString();
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
