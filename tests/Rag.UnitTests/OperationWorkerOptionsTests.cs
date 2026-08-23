using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class OperationWorkerOptionsTests
{
    [Fact]
    public void Default_lease_duration_is_five_minutes()
    {
        var options = new OperationWorkerOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
    }
}
