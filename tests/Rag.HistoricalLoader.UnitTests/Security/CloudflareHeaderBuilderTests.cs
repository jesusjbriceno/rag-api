using Rag.HistoricalLoader.Engine.Security;

namespace Rag.HistoricalLoader.UnitTests.Security;

public sealed class CloudflareHeaderBuilderTests
{
    [Fact]
    public void Apply_adds_the_service_token_header_pair()
    {
        var request = new HttpRequestMessage();
        var builder = new CloudflareHeaderBuilder();

        builder.Apply(request, new CloudflareServiceToken("cf-id", "cf-secret"));

        Assert.Equal("cf-id", request.Headers.GetValues(CloudflareHeaderBuilder.ClientIdHeader).Single());
        Assert.Equal("cf-secret", request.Headers.GetValues(CloudflareHeaderBuilder.ClientSecretHeader).Single());
    }

    [Fact]
    public void Builder_is_stateless_and_retains_no_secret()
    {
        var secretMembers = typeof(CloudflareHeaderBuilder)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Cast<System.Reflection.MemberInfo>()
            .Concat(typeof(CloudflareHeaderBuilder).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            .ToArray();

        Assert.Empty(secretMembers);
    }
}
