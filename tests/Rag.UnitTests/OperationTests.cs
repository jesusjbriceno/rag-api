using Rag.Domain;

namespace Rag.UnitTests;

public sealed class OperationTests
{
    [Fact]
    public void Pending_operation_can_be_claimed_and_succeed()
    {
        var claimedAt = DateTimeOffset.UtcNow;
        var operation = Operation.CreatePending(Guid.NewGuid(), claimedAt);

        operation.Claim("worker-a", claimedAt, claimedAt.AddMinutes(5));
        operation.Succeed(claimedAt.AddMinutes(1));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.NotNull(operation.StartedAt);
        Assert.NotNull(operation.CompletedAt);
        Assert.Null(operation.LeaseOwner);
        Assert.Null(operation.LeaseExpiresAt);
    }

    [Fact]
    public void Running_operation_can_fail_with_traceable_details()
    {
        var claimedAt = DateTimeOffset.UtcNow;
        var operation = Operation.CreatePending(Guid.NewGuid(), claimedAt);
        operation.Claim("worker-a", claimedAt, claimedAt.AddMinutes(5));

        operation.Fail("parse", "The TXT content could not be parsed.", claimedAt.AddMinutes(1));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("parse", operation.FailureStage);
        Assert.Equal("The TXT content could not be parsed.", operation.FailureMessage);
        Assert.Null(operation.LeaseOwner);
        Assert.Null(operation.LeaseExpiresAt);
    }

    [Fact]
    public void Terminal_operation_cannot_advance()
    {
        var claimedAt = DateTimeOffset.UtcNow;
        var operation = Operation.CreatePending(Guid.NewGuid(), claimedAt);
        operation.Claim("worker-a", claimedAt, claimedAt.AddMinutes(5));
        operation.Succeed(claimedAt.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => operation.Claim("worker-a", claimedAt, claimedAt.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => operation.Succeed(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Expired_running_operation_can_be_reclaimed_by_another_worker()
    {
        var claimedAt = DateTimeOffset.UtcNow;
        var operation = Operation.CreatePending(Guid.NewGuid(), claimedAt);
        operation.Claim("worker-a", claimedAt, claimedAt.AddMinutes(1));

        operation.Claim("worker-b", claimedAt.AddMinutes(2), claimedAt.AddMinutes(7));

        Assert.Equal(OperationStatus.Running, operation.Status);
        Assert.Equal("worker-b", operation.LeaseOwner);
        Assert.Equal(claimedAt.AddMinutes(7), operation.LeaseExpiresAt);
    }

    [Fact]
    public void Active_running_operation_cannot_be_reclaimed()
    {
        var claimedAt = DateTimeOffset.UtcNow;
        var operation = Operation.CreatePending(Guid.NewGuid(), claimedAt);
        operation.Claim("worker-a", claimedAt, claimedAt.AddMinutes(5));

        Assert.Throws<InvalidOperationException>(() => operation.Claim("worker-b", claimedAt.AddMinutes(1), claimedAt.AddMinutes(6)));
    }
}
