using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.UnitTests.Inventory;

public sealed class CandidateClassifierTests
{
    public static TheoryData<CandidateDiscovery, string, string?> Cases => new()
    {
        { new CandidateDiscovery("a.doc", 5, false, null), EligibilityCodes.UnsupportedFormat, ".doc" },
        { new CandidateDiscovery("b.docx", 5, false, null), EligibilityCodes.Eligible, null },
        { new CandidateDiscovery("c.md", 5, false, null), EligibilityCodes.Eligible, null },
        { new CandidateDiscovery("d.pdf", 5, false, null), EligibilityCodes.Eligible, null },
        { new CandidateDiscovery("e.txt", 5, false, null), EligibilityCodes.Eligible, null },
        { new CandidateDiscovery("f.DOCX", 5, false, null), EligibilityCodes.Eligible, null },
        { new CandidateDiscovery("g.xlsx", 5, false, null), EligibilityCodes.UnsupportedFormat, ".xlsx" },
        { new CandidateDiscovery("h", 5, false, null), EligibilityCodes.UnsupportedFormat, "" },
        { new CandidateDiscovery("i.docx", 11, false, null), EligibilityCodes.SizePolicyExceeded, null },
        { new CandidateDiscovery("j.docx", 5, false, "E_ACCESS"), EligibilityCodes.AccessDenied, "E_ACCESS" },
        { new CandidateDiscovery("k.docx", 5, true, null), EligibilityCodes.ReparsePoint, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_ProducesStableEligibilityCode(CandidateDiscovery discovery, string expectedCode, string? expectedError)
    {
        var classifier = new CandidateClassifier(maxByteSize: 10);

        var result = classifier.Classify(discovery);

        Assert.Equal(expectedCode, result.EligibilityCode);
        Assert.Equal(expectedError, result.DiscoveryErrorCode);
    }

    [Fact]
    public void Classify_WiresResultIntoCandidateEntity()
    {
        var classifier = new CandidateClassifier(maxByteSize: 10);
        var result = classifier.Classify(new CandidateDiscovery("report.pdf", 5, false, null));

        var candidate = new Candidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "report.pdf",
            ".pdf",
            5,
            DateTimeOffset.UtcNow,
            result.EligibilityCode,
            result.DiscoveryErrorCode);

        Assert.Equal(EligibilityCodes.Eligible, candidate.EligibilityCode);
        Assert.Null(candidate.DiscoveryErrorCode);
    }
}
