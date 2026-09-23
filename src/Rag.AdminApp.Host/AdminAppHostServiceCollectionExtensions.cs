using Microsoft.Extensions.Options;
using Rag.AdminApp;
using Rag.AdminApp.Host.Configuration;
using Rag.Infrastructure;

namespace Rag.AdminApp.Host;

public static class AdminAppHostServiceCollectionExtensions
{
    private const string AllowedAdminSubjectsSection = "AllowedAdminSubjects";

    public static IServiceCollection AddAdminAppHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CloudflareAccessHostOptions>()
            .Bind(configuration.GetSection(CloudflareAccessHostOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CloudflareAccessHostOptions>, CloudflareAccessHostOptions>();

        services.AddOptions<AdminAssertionHostOptions>()
            .Bind(configuration.GetSection(AdminAssertionHostOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminAssertionHostOptions>, AdminAssertionHostOptions>();

        services.AddOptions<AdminAppAuthOptions>()
            .Bind(configuration.GetSection(AdminAppAuthOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminAppAuthOptions>, AdminAppAuthHostValidator>();

        services.AddOptions<AdminApiHostOptions>()
            .Bind(configuration.GetSection(AdminApiHostOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminApiHostOptions>, AdminApiHostOptions>();

        var allowedSubjects = configuration.GetSection(AllowedAdminSubjectsSection).Get<string[]>() ?? [];
        if (allowedSubjects.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Allowed admin subjects must be non-blank.");
        }

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CloudflareAccessHostOptions>>().Value.ToOptions());
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AdminAssertionHostOptions>>().Value.ToOptions());
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AdminAppAuthOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AdminApiHostOptions>>().Value);
        services.AddSingleton<IReadOnlySet<string>>(new HashSet<string>(allowedSubjects, StringComparer.Ordinal));

        // BFF runtime: Cloudflare validation, allowlist, assertion/proof signer, and
        // the closed proxy/health clients. Registration stays additive so later work
        // units (SPA serving, historical import) can extend without replacing it.
        services.AddSingleton(sp => new CloudflareAccessValidator(sp.GetRequiredService<CloudflareAccessOptions>()));
        services.AddSingleton(sp => new AdminAssertionIssuer(sp.GetRequiredService<AdminAssertionIssuerOptions>()));
        services.AddSingleton<AdminProxyRequestSigner>();
        services.AddSingleton<AdminSubjectAllowlist>();
        services.AddSingleton<CloudflareAssertionHeaderReader>();
        services.AddTransient<CloudflareAdminEndpointFilter>();

        services.AddHttpClient<AdminApiClient>((sp, client) =>
        {
            client.BaseAddress = new Uri(sp.GetRequiredService<AdminApiHostOptions>().BaseUrl!);
        });
        services.AddHttpClient<AdminApiHealthProbe>((sp, client) =>
        {
            client.BaseAddress = new Uri(sp.GetRequiredService<AdminApiHostOptions>().BaseUrl!);
        });

        return services;
    }
}
