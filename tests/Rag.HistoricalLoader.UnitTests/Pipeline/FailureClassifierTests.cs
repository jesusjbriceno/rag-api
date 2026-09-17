using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.UnitTests.Pipeline;

public sealed class FailureClassifierTests
{
    [Theory]
    [InlineData(ApiOutcome.TransientFailure)]
    [InlineData(ApiOutcome.UnknownOutcome)]
    public void ApiOutcome_TransientAndUnknown_ClassifiedTransientNetwork(ApiOutcome outcome)
        => Assert.Equal(FailureClass.TransientNetwork, FailureClassifier.ClassifyApiOutcome(outcome));

    [Fact]
    public void ApiOutcome_Auth_ClassifiedAuthentication()
        => Assert.Equal(FailureClass.Authentication, FailureClassifier.ClassifyApiOutcome(ApiOutcome.AuthFailure));

    [Fact]
    public void ApiOutcome_Contract_ClassifiedContractData()
        => Assert.Equal(FailureClass.ContractData, FailureClassifier.ClassifyApiOutcome(ApiOutcome.ContractFailure));

    [Fact]
    public void ApiOutcome_Success_ClassifiedNone()
        => Assert.Equal(FailureClass.None, FailureClassifier.ClassifyApiOutcome(ApiOutcome.Success));

    [Theory]
    [InlineData(ExtractionOutcome.SkippedDocument)]
    [InlineData(ExtractionOutcome.Error)]
    public void Extraction_NonCompleted_ClassifiedDocument(ExtractionOutcome outcome)
        => Assert.Equal(FailureClass.Document, FailureClassifier.ClassifyExtractionOutcome(outcome));

    [Fact]
    public void Extraction_Completed_ClassifiedNone()
        => Assert.Equal(FailureClass.None, FailureClassifier.ClassifyExtractionOutcome(ExtractionOutcome.Completed));

    [Fact]
    public void Exception_Cancellation_ClassifiedNone()
        => Assert.Equal(FailureClass.None, FailureClassifier.ClassifyException(new OperationCanceledException()));

    [Fact]
    public void Exception_Infrastructure_ClassifiedLocalCapacity()
        => Assert.Equal(FailureClass.LocalCapacity, FailureClassifier.ClassifyException(new InvalidOperationException("sqlite failure")));

    // Transient HTTP status codes all surface as ApiOutcome.TransientFailure and classify as network.
    [Theory]
    [InlineData("timeout")]
    [InlineData("429")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    public void TransientStatusCodes_ClassifyTransientNetwork(string errorCode)
    {
        var result = new ApiOperationResult(ApiOutcome.TransientFailure, ErrorCode: errorCode);

        Assert.Equal(FailureClass.TransientNetwork, FailureClassifier.ClassifyApiOutcome(result.Outcome));
    }
}
