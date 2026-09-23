using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rag.AdminApp.Host;
using Rag.AdminApp.Host.Configuration;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAppHostConfigurationTests
{
    private static readonly RSA CloudflareKey = RSA.Create(2048);
    private static readonly RSA AssertionKey = RSA.Create(2048);

    [Theory]
    [InlineData("CloudflareAccess:Issuer")]
    [InlineData("CloudflareAccess:Audience")]
    [InlineData("CloudflareAccess:Keys:0:KeyId")]
    [InlineData("CloudflareAccess:Keys:0:PublicKeyPem")]
    [InlineData("AdminAssertion:Issuer")]
    [InlineData("AdminAssertion:Audience")]
    [InlineData("AdminAssertion:AppId")]
    [InlineData("AdminAssertion:KeyId")]
    [InlineData("AdminAssertion:PrivateKeyPem")]
    [InlineData("AdminAppAuth:Apps:0:AppId")]
    [InlineData("AdminAppAuth:Apps:0:KeyId")]
    [InlineData("AdminAppAuth:Apps:0:CurrentSecret")]
    [InlineData("AdminApi:BaseUrl")]
    public void Missing_required_setting_fails_startup(string key)
    {
        var values = ValidConfiguration();
        values.Remove(key);

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Empty_cloudflare_access_keys_fail_startup()
    {
        var values = ValidConfiguration();
        values.Remove("CloudflareAccess:Keys:0:KeyId");
        values.Remove("CloudflareAccess:Keys:0:PublicKeyPem");

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Duplicate_cloudflare_access_key_ids_fail_startup()
    {
        var values = ValidConfiguration();
        values["CloudflareAccess:Keys:1:KeyId"] = values["CloudflareAccess:Keys:0:KeyId"]!;
        values["CloudflareAccess:Keys:1:PublicKeyPem"] = values["CloudflareAccess:Keys:0:PublicKeyPem"]!;

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Malformed_cloudflare_access_public_key_fails_startup()
    {
        var values = ValidConfiguration();
        values["CloudflareAccess:Keys:0:PublicKeyPem"] = "not-a-pem";

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Malformed_admin_assertion_private_key_fails_startup()
    {
        var values = ValidConfiguration();
        values["AdminAssertion:PrivateKeyPem"] = "not-a-pem";

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Empty_admin_app_auth_apps_fail_startup()
    {
        var values = ValidConfiguration();
        values.Remove("AdminAppAuth:Apps:0:AppId");
        values.Remove("AdminAppAuth:Apps:0:KeyId");
        values.Remove("AdminAppAuth:Apps:0:CurrentSecret");

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Duplicate_admin_app_ids_fail_startup()
    {
        var values = ValidConfiguration();
        values["AdminAppAuth:Apps:1:AppId"] = values["AdminAppAuth:Apps:0:AppId"]!;
        values["AdminAppAuth:Apps:1:KeyId"] = "machine-key-2";
        values["AdminAppAuth:Apps:1:CurrentSecret"] = "another-secret";

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Assertion_app_id_matching_no_app_fails_startup()
    {
        var values = ValidConfiguration();
        values["AdminAssertion:AppId"] = "unmatched-app";

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://example.com/")]
    [InlineData("https://example.com/?query=1")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("https://example.com/api")]
    public void Malformed_admin_api_base_url_fails_startup(string baseUrl)
    {
        var values = ValidConfiguration();
        values["AdminApi:BaseUrl"] = baseUrl;

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Blank_allowed_admin_subject_fails_startup()
    {
        var values = ValidConfiguration();
        values["AllowedAdminSubjects:0"] = " ";

        Assert.ThrowsAny<Exception>(() => StartHost(values));
    }

    [Fact]
    public void Valid_configuration_resolves_all_options_and_absent_allowlist_is_empty()
    {
        using var app = BuildHost(ValidConfiguration());

        _ = app.Services.GetRequiredService<IOptions<CloudflareAccessHostOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<AdminAssertionHostOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<AdminAppAuthOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<AdminApiHostOptions>>().Value;

        Assert.Empty(app.Services.GetRequiredService<IReadOnlySet<string>>());
    }

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["CloudflareAccess:Issuer"] = "https://team.cloudflareaccess.com",
        ["CloudflareAccess:Audience"] = "test-audience",
        ["CloudflareAccess:Keys:0:KeyId"] = "cf-key-1",
        ["CloudflareAccess:Keys:0:PublicKeyPem"] = CloudflareKey.ExportRSAPublicKeyPem(),
        ["AdminAssertion:Issuer"] = "admin-issuer",
        ["AdminAssertion:Audience"] = "admin-audience",
        ["AdminAssertion:AppId"] = "admin-app",
        ["AdminAssertion:KeyId"] = "assertion-key-1",
        ["AdminAssertion:PrivateKeyPem"] = AssertionKey.ExportRSAPrivateKeyPem(),
        ["AdminAppAuth:Apps:0:AppId"] = "admin-app",
        ["AdminAppAuth:Apps:0:KeyId"] = "machine-key-1",
        ["AdminAppAuth:Apps:0:CurrentSecret"] = "machine-secret",
        ["AdminApi:BaseUrl"] = "https://api.internal/",
    };

    private static void StartHost(Dictionary<string, string?> values)
    {
        using var app = BuildHost(values);
        app.Start();
    }

    private static WebApplication BuildHost(Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddAdminAppHost(builder.Configuration);
        var app = builder.Build();
        app.MapAdminAppHost();
        return app;
    }
}
