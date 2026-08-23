using Rag.Domain;

namespace Rag.UnitTests;

public sealed class OperationTests
{
    [Fact]
    public void Pending_operation_can_run_and_succeed()
    {
        var operation = Operation.CreatePending(Guid.NewGuid(), DateTimeOffset.UtcNow);

        operation.Start(DateTimeOffset.UtcNow);
        operation.Succeed(DateTimeOffset.UtcNow);

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.NotNull(operation.StartedAt);
        Assert.NotNull(operation.CompletedAt);
    }

    [Fact]
    public void Running_operation_can_fail_with_traceable_details()
    {
        var operation = Operation.CreatePending(Guid.NewGuid(), DateTimeOffset.UtcNow);
        operation.Start(DateTimeOffset.UtcNow);

        operation.Fail("parse", "The TXT content could not be parsed.", DateTimeOffset.UtcNow);

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("parse", operation.FailureStage);
        Assert.Equal("The TXT content could not be parsed.", operation.FailureMessage);
    }

    [Fact]
    public void Terminal_operation_cannot_advance()
    {
        var operation = Operation.CreatePending(Guid.NewGuid(), DateTimeOffset.UtcNow);
        operation.Start(DateTimeOffset.UtcNow);
        operation.Succeed(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => operation.Start(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => operation.Succeed(DateTimeOffset.UtcNow));
    }
}
