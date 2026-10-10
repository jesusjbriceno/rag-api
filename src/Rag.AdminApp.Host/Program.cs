using Microsoft.Extensions.FileProviders;
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

        // The SPA shell and its assets sit behind the exact same Cloudflare identity +
        // subject-allowlist guard as the BFF operations: /health is the only route the
        // guard middleware lets through unauthenticated, and the two fixed /api/admin
        // routes guard themselves. Without a built SPA the host fails closed (404).
        var spaRoot = ResolveSpaRoot(bffOptions.SpaRoot, app.Environment.ContentRootPath);
        if (spaRoot is null)
        {
            app.Logger.LogWarning(
                "No built admin SPA found (set AdminBff:SpaRoot); the admin shell is not served.");
        }
        else
        {
            var fileProvider = new PhysicalFileProvider(spaRoot);
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                // Exact-path exemption only: /health/<anything> must not bypass the
                // guard and reach the static files or the SPA fallback. PathString
                // equality is ordinal-ignore-case, matching how routing maps /health.
                if (path == "/health" || path.StartsWithSegments("/api"))
                {
                    await next(context);
                    return;
                }

                var (subject, problem) = AdminBrowserGuard.Authorize(context, app.Services.GetRequiredService<CloudflareAccessValidator>(), app.Services.GetRequiredService<AdminBffOptions>());
                if (problem is not null)
                {
                    await problem.ExecuteAsync(context);
                    return;
                }

                await next(context);
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                ServeUnknownFileTypes = false,
            });
            // The SPA fallback serves the shell only: an unmatched /api path must stay a
            // plain 404 so the fixed two-operation BFF surface never grows silently.
            app.MapFallback((HttpContext context) =>
                context.Request.Path.StartsWithSegments("/api")
                    ? Results.NotFound()
                    : Results.Stream(File.OpenRead(Path.Combine(spaRoot, "index.html")), "text/html"));
        }

        app.Run();
    }

    /// <summary>
    /// Locates the built admin SPA. An explicit configured path wins; otherwise the
    /// repository's conventional apps/admin-ui/dist is searched upward from both the
    /// content root and the binary directory (dev runs and the test host differ).
    /// Returns null when no built SPA exists, so the host fails closed.
    /// </summary>
    internal static string? ResolveSpaRoot(string? configured, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            return Directory.Exists(full) ? full : null;
        }

        foreach (var root in new[] { contentRoot, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; depth < 8 && directory is not null; depth++)
            {
                var candidate = Path.Combine(directory.FullName, "apps", "admin-ui", "dist");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}
