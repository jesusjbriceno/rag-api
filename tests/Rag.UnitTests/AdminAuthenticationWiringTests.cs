using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAuthenticationWiringTests
{
    [Fact]
    public void Disabled_admin_plane_does_not_register_admin_services()
    {
        using var provider = BuildProvider(enabled: false, withAdminConfig: false);

        Assert.Null(provider.GetService<AdminAuthenticator>());
        Assert.Null(provider.GetService<AdminAssertionValidator>());
        Assert.Null(provider.GetService<AdminAssertionKeyRing>());
        Assert.Null(provider.GetService<AdminMachineProofVerifier>());
        Assert.Null(provider.GetService<IAdminAssertionReplayRepository>());
    }

    [Fact]
    public void Enabled_admin_plane_without_required_configuration_fails_validation()
    {
        using var provider = BuildProvider(enabled: true, withAdminConfig: false);

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminAssertionOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminAppAuthOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminAuditOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminOperationsOptions>>().Value);
    }

    [Fact]
    public void Enabled_admin_plane_with_valid_configuration_registers_admin_services()
    {
        using var provider = BuildProvider(enabled: true, withAdminConfig: true);

        Assert.NotNull(provider.GetService<AdminAuthenticator>());
        Assert.NotNull(provider.GetService<AdminAssertionValidator>());
        Assert.NotNull(provider.GetService<AdminAssertionKeyRing>());
        Assert.NotNull(provider.GetService<AdminMachineProofVerifier>());
        Assert.NotNull(provider.GetService<IAdminAssertionReplayRepository>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is AdminRetentionWorker);
        Assert.Equal("days", provider.GetRequiredService<IOptions<AdminAuditOptions>>().Value.NormalizedMode);
    }

    private static ServiceProvider BuildProvider(bool enabled, bool withAdminConfig)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(BuildConfiguration(enabled, withAdminConfig));
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(bool enabled, bool withAdminConfig)
    {
        using var jwtKey = RSA.Create(2048);
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Rag"] = "Host=localhost;Database=wiring;Username=wiring;Password=wiring",
            ["Jwt:Issuer"] = "test-issuer",
            ["Jwt:Audience"] = "test-audience",
            ["Jwt:CurrentSigningKey:KeyId"] = "jwt-key-1",
            ["Jwt:CurrentSigningKey:PrivateKeyPem"] = jwtKey.ExportRSAPrivateKeyPem(),
            ["Jwt:ValidationKeys:0:KeyId"] = "jwt-key-1",
            ["Jwt:ValidationKeys:0:PublicKeyPem"] = jwtKey.ExportRSAPublicKeyPem(),
            ["AdminPlane:Enabled"] = enabled ? "true" : "false",
        };

        var profile = EmbeddingProfile.Default;
        values["Embeddings:AllowedProfiles:0:Provider"] = profile.Provider;
        values["Embeddings:AllowedProfiles:0:Model"] = profile.Model;
        values["Embeddings:AllowedProfiles:0:Version"] = profile.Version;
        values["Embeddings:AllowedProfiles:0:Dimensions"] = profile.Dimensions.ToString(CultureInfo.InvariantCulture);

        if (withAdminConfig)
        {
            using var assertionKey = RSA.Create(2048);
            values["AdminAssertion:Issuer"] = "admin-issuer";
            values["AdminAssertion:Audience"] = "admin-audience";
            values["AdminAssertion:ValidationKeys:0:KeyId"] = "assertion-key-1";
            values["AdminAssertion:ValidationKeys:0:PublicKeyPem"] = assertionKey.ExportRSAPublicKeyPem();
            values["AdminAppAuth:Apps:0:AppId"] = "admin-app";
            values["AdminAppAuth:Apps:0:KeyId"] = "machine-key-1";
            values["AdminAppAuth:Apps:0:CurrentSecret"] = "machine-secret";
            values["AdminAudit:RetentionMode"] = "days";
            values["AdminAudit:RetentionDays"] = "30";
            values["AdminOperations:RetentionHours"] = "24";
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
