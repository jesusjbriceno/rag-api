using Rag.HistoricalLoader.Core.Benchmark;

namespace Rag.HistoricalLoader.UnitTests.Benchmark;

public sealed class LocalExtractionTests
{
    [Theory]
    [InlineData(".doc", true)]
    [InlineData(".DOC", true)]
    [InlineData(".docx", false)]
    [InlineData(".pdf", false)]
    [InlineData(".md", false)]
    [InlineData(".txt", false)]
    public void LocalExtractionPolicy_RequiresLibreOffice_ClassifiesExtensions(string extension, bool expected)
        => Assert.Equal(expected, LocalExtractionPolicy.RequiresLibreOffice(extension));

    [Fact]
    public async Task RealExtractionPhases_Extract_RecordsRealNormalizedBytesAndOutcome()
    {
        var phase = RealExtractionPhases.Extract(new FakeExtractor(new LocalExtractionResult(1234, "completed", null)));
        var candidate = new BenchmarkCandidate(Guid.NewGuid(), ".pdf|1", 5000, "/corpus/sample.pdf");

        var result = await phase(candidate, CancellationToken.None);

        Assert.Equal(1234, result.Bytes);
        Assert.Equal("completed", result.Outcome);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task RealExtractionPhases_Extract_ReturnsSourcePathUnavailableWhenPathMissing()
    {
        var phase = RealExtractionPhases.Extract(new FakeExtractor(new LocalExtractionResult(0, "completed", null)));
        var candidate = new BenchmarkCandidate(Guid.NewGuid(), ".pdf|1", 5000, null);

        var result = await phase(candidate, CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.Equal("error", result.Outcome);
        Assert.Equal(LocalExtractionErrorCodes.SourcePathUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task RealExtractionPhases_Extract_PassesThroughUnavailableErrorCode()
    {
        var phase = RealExtractionPhases.Extract(new FakeExtractor(
            new LocalExtractionResult(0, "error", LocalExtractionErrorCodes.UnavailableLibreOfficeAbsent)));
        var candidate = new BenchmarkCandidate(Guid.NewGuid(), ".doc|1", 5000, "/corpus/sample.doc");

        var result = await phase(candidate, CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.Equal("error", result.Outcome);
        Assert.Equal(LocalExtractionErrorCodes.UnavailableLibreOfficeAbsent, result.ErrorCode);
    }

    private sealed class FakeExtractor(LocalExtractionResult result) : ILocalTextExtractor
    {
        public Task<LocalExtractionResult> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
