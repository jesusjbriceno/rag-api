using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Sampling;

namespace Rag.HistoricalLoader.UnitTests.Sampling;

public sealed class StratifiedSampleSelectorTests
{
    private static readonly Guid ManifestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RootId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Select_StratifiesByObservedFormatAndEmpiricalSizeBand()
    {
        var candidates = new[]
        {
            Candidate(1, ".docx", 100), Candidate(2, ".docx", 200),
            Candidate(3, ".pdf", 300), Candidate(4, ".pdf", 400),
            Candidate(5, ".docx", 1000), Candidate(6, ".docx", 2000),
            Candidate(7, ".pdf", 3000), Candidate(8, ".pdf", 4000),
        };

        var result = new StratifiedSampleSelector().Select(Request(budget: 8, minPerStratum: 1, sizeBands: 2), candidates);

        Assert.True(result.GatePassed);
        Assert.Equal(4, result.Coverage.Count);
        Assert.Contains(result.Coverage, c => c.Format == ".docx" && c.SizeBand == "band_1" && c.Population == 2);
        Assert.Contains(result.Coverage, c => c.Format == ".docx" && c.SizeBand == "band_2" && c.Population == 2);
        Assert.Contains(result.Coverage, c => c.Format == ".pdf" && c.SizeBand == "band_1" && c.Population == 2);
        Assert.Contains(result.Coverage, c => c.Format == ".pdf" && c.SizeBand == "band_2" && c.Population == 2);
    }

    [Fact]
    public void Select_AllocatesProportionallyWithRecordedMinimum()
    {
        var candidates = Enumerable.Range(1, 90).Select(i => Candidate(i, ".docx", 100))
            .Concat(Enumerable.Range(101, 10).Select(i => Candidate(i, ".pdf", 100)))
            .ToArray();

        var result = new StratifiedSampleSelector().Select(Request(budget: 20, minPerStratum: 1), candidates);

        Assert.True(result.GatePassed);
        var docx = result.Coverage.Single(c => c.Format == ".docx");
        var pdf = result.Coverage.Single(c => c.Format == ".pdf");
        Assert.True(docx.Allocation > pdf.Allocation);
        Assert.True(docx.Allocation >= 1 && pdf.Allocation >= 1);
        Assert.Equal(20, result.Coverage.Sum(c => c.Allocation));
    }

    [Fact]
    public void Select_IsDeterministicForSameSeed()
    {
        var candidates = Enumerable.Range(1, 50).Select(i => Candidate(i, ".docx", 100 + i)).ToArray();
        var selector = new StratifiedSampleSelector();
        var request = Request(budget: 10, minPerStratum: 1, seed: 42);

        var first = selector.Select(request, candidates);
        var second = selector.Select(request, candidates);

        Assert.Equal(first.Members.Select(m => m.CandidateId), second.Members.Select(m => m.CandidateId));
        Assert.Equal(first.Members.Select(m => m.MetadataFingerprint), second.Members.Select(m => m.MetadataFingerprint));
        Assert.Equal(first.SampleSet.Id, second.SampleSet.Id);
    }

    [Fact]
    public void Select_ChangesDeterministicallyWithSeed()
    {
        var candidates = Enumerable.Range(1, 100).Select(i => Candidate(i, ".txt", 100 + i)).ToArray();
        var selector = new StratifiedSampleSelector();

        var a = selector.Select(Request(budget: 5, minPerStratum: 1, seed: 1), candidates);
        var b = selector.Select(Request(budget: 5, minPerStratum: 1, seed: 2), candidates);

        Assert.NotEqual(a.Members.Select(m => m.CandidateId), b.Members.Select(m => m.CandidateId));
    }

    [Fact]
    public void Select_FreezesMetadataFingerprints()
    {
        var candidates = Enumerable.Range(1, 20).Select(i => Candidate(i, ".md", 100 + i)).ToArray();

        var result = new StratifiedSampleSelector().Select(Request(budget: 6, minPerStratum: 1), candidates);

        Assert.NotEmpty(result.Members);
        var byId = candidates.ToDictionary(c => c.Id);
        Assert.All(result.Members, m =>
            Assert.Equal(StratifiedSampleSelector.ComputeMetadataFingerprint(byId[m.CandidateId]), m.MetadataFingerprint));
    }

    [Fact]
    public void Select_FailsGateWhenBudgetCannotCoverEveryStratum()
    {
        var candidates = new[]
        {
            Candidate(1, ".docx", 100), Candidate(2, ".pdf", 200), Candidate(3, ".txt", 300),
        };

        var result = new StratifiedSampleSelector().Select(Request(budget: 2, minPerStratum: 1), candidates);

        Assert.False(result.GatePassed);
        Assert.False(result.Representative);
        Assert.NotNull(result.GateFailureReason);
        Assert.Contains(result.Coverage, c => !c.Covered);
        Assert.Empty(result.Members);
    }

    [Fact]
    public void Select_RejectsIncompleteManifest()
    {
        var candidates = new[] { Candidate(1, ".docx", 100) };
        var selector = new StratifiedSampleSelector();
        var request = Request(budget: 1, minPerStratum: 1) with { ManifestState = ManifestState.Scanning };

        var exception = Assert.Throws<InvalidOperationException>(() => selector.Select(request, candidates));
        Assert.Contains("not complete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_DoesNotClaimRepresentativenessWithoutOperatorParameters()
    {
        var candidates = new[] { Candidate(1, ".docx", 100), Candidate(2, ".docx", 200) };
        var selector = new StratifiedSampleSelector();
        var request = Request(budget: 2, minPerStratum: 1) with { ConfidenceCoverageRules = string.Empty };

        var result = selector.Select(request, candidates);

        Assert.True(result.GatePassed);
        Assert.False(result.Representative);
        Assert.Contains(result.Limitations, l => l.Contains("representative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Select_ReportsOutlierAndMissingBandLimitations()
    {
        var candidates = new[]
        {
            Candidate(1, ".docx", 100), Candidate(2, ".docx", 100), Candidate(3, ".docx", 100),
            Candidate(4, ".docx", 1_000_000),
        };

        var result = new StratifiedSampleSelector().Select(Request(budget: 4, minPerStratum: 1, sizeBands: 4), candidates);

        Assert.True(result.GatePassed);
        Assert.Contains(result.Limitations, l => l.Contains("size band", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Limitations, l => l.Contains("outlier", StringComparison.OrdinalIgnoreCase));
    }

    private static SampleSelectionRequest Request(
        int budget,
        int minPerStratum,
        int sizeBands = 4,
        long seed = 12345,
        string coverageRules = "{\"confidence\":0.95}")
        => new(ManifestId, ManifestState.Complete, budget, seed, minPerStratum, "splitmix64", coverageRules, sizeBands);

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
}
