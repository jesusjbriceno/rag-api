using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAuthenticationContractTests
{
    [Fact]
    public void Contract_defines_the_six_downstream_wire_header_names()
    {
        Assert.Equal("X-Admin-App-Id", AdminAuthenticationContract.AppIdHeader);
        Assert.Equal("X-Admin-Key-Id", AdminAuthenticationContract.KeyIdHeader);
        Assert.Equal("X-Admin-Timestamp", AdminAuthenticationContract.TimestampHeader);
        Assert.Equal("X-Admin-Signature", AdminAuthenticationContract.SignatureHeader);
        Assert.Equal("X-Admin-Assertion", AdminAuthenticationContract.AssertionHeader);
        Assert.Equal("Idempotency-Key", AdminAuthenticationContract.IdempotencyKeyHeader);
    }

    [Fact]
    public void Contract_defines_the_one_mib_authenticated_body_limit()
    {
        Assert.Equal(1_048_576, AdminAuthenticationContract.MaxBodyBytes);
    }
}
