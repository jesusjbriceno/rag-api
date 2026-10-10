using Rag.AdminApp;

namespace Rag.AdminApp.Host;

// Namespaced entry point: keeps Rag.Api's top-level global::Program unambiguous for
// test factories in assemblies that reference both web projects.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var bffOptions = builder.Configuration.GetSection(AdminBffOptions.SectionName).Get<AdminBffOptions>()
            ?? new AdminBffOptions();
        bffOptions.Validate();

        var cloudflareOptions = builder.Configuration.GetSection("CloudflareAccess").Get<CloudflareAccessOptions>()
            ?? throw new InvalidOperationException("Cloudflare Access validation options must be configured.");
        var assertionOptions = builder.Configuration.GetSection("AdminAssertion").Get<AdminAssertionIssuerOptions>()
            ?? throw new InvalidOperationException("Admin assertion options must be configured.");
        var signerOptions = builder.Configuration.GetSection("AdminBff:Signing").Get<AdminRequestSignerOptions>()
            ?? throw new InvalidOperationException("Admin request signing options must be configured.");

        builder.Services.AddSingleton(bffOptions);
        builder.Services.AddSingleton(new CloudflareAccessValidator(cloudflareOptions));
        builder.Services.AddSingleton(new AdminAssertionIssuer(assertionOptions));
        builder.Services.AddSingleton(sp => new AdminRequestSigner(
            sp.GetRequiredService<AdminAssertionIssuer>(), signerOptions));

        builder.Services.AddHttpClient(AdminApiProxyClient.ClientName, client =>
            client.BaseAddress = new Uri(bffOptions.UpstreamBaseUrl!));
        builder.Services.AddScoped<AdminApiProxyClient>(sp => new AdminApiProxyClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AdminApiProxyClient.ClientName),
            sp.GetRequiredService<AdminRequestSigner>(),
            sp.GetRequiredService<ILogger<AdminApiProxyClient>>()));

        var app = builder.Build();

        // Health probing stays separate from browser routes: no identity requirement, no proxying.
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        // The entire browser surface is exactly the two operations of the first-key workflow.
        // Paths, methods, and headers are fixed here; the browser can never select a target.
        app.MapPost("/api/admin/clients", (HttpContext context, CloudflareAccessValidator validator,
                AdminBffOptions options, AdminApiProxyClient proxy, CancellationToken cancellationToken) =>
            AdminBffRoutes.ProxyAsync(context, "/api/v1/admin/clients", validator, options, proxy, cancellationToken));

        app.MapPost("/api/admin/clients/{clientId:guid}/credentials", (HttpContext context, Guid clientId,
                CloudflareAccessValidator validator, AdminBffOptions options, AdminApiProxyClient proxy,
                CancellationToken cancellationToken) =>
            AdminBffRoutes.ProxyAsync(context, $"/api/v1/admin/clients/{clientId}/credentials", validator, options, proxy, cancellationToken));

        app.Run();
    }
}
