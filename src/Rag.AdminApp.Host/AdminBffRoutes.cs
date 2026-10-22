using Rag.AdminApp;

namespace Rag.AdminApp.Host;

/// <summary>
/// The guarded browser-to-upstream pipeline, shared by the fixed proxy routes:
/// authorize the Cloudflare identity, enforce same-origin for mutations, read a bounded
/// body, require a UUID idempotency key per logical mutation, sign server-side, and
/// forward only the upstream response's status, content type, and body. Cross-site,
/// unauthenticated, or keyless requests never build an upstream call.
/// </summary>
internal static class AdminBffRoutes
{
    // Mirrors the admin API's MaxAdminBodyBytes so oversize bodies fail here first.
    private const int MaxBodyBytes = 1_048_576;

    public static async Task<IResult> ProxyAsync(
        HttpContext context,
        string upstreamPathAndQuery,
        CloudflareAccessValidator validator,
        AdminBffOptions options,
        AdminApiProxyClient proxy,
        CancellationToken cancellationToken)
    {
        var (subject, identityProblem) = AdminBrowserGuard.Authorize(context, validator, options);
        if (identityProblem is not null)
        {
            return identityProblem;
        }

        if (AdminBrowserGuard.RequireSameOrigin(context, options) is { } crossOriginProblem)
        {
            return crossOriginProblem;
        }

        var body = await ReadBodyAsync(context, cancellationToken);
        if (body is null)
        {
            return AdminBffProblems.Create(StatusCodes.Status413PayloadTooLarge, "body_too_large");
        }

        // One UUID Idempotency-Key per logical browser mutation: validated here and
        // forwarded unchanged so upstream retries deduplicate instead of duplicating.
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        if (!Guid.TryParseExact(idempotencyKey, "D", out _))
        {
            return AdminBffProblems.Create(StatusCodes.Status400BadRequest, "invalid_idempotency_key");
        }

        var response = await proxy.ForwardAsync(subject, "POST", upstreamPathAndQuery, body, idempotencyKey, cancellationToken);
        if (response is null || response.StatusCode >= 500)
        {
            // Redacted failure: upstream internals, bodies, and auth material never leak.
            return AdminBffProblems.Create(StatusCodes.Status502BadGateway, "upstream_unavailable");
        }

        // Forward only the status, content type, and body: upstream response headers
        // (cookies, auth material, redirects) are never relayed to the browser.
        context.Response.StatusCode = response.StatusCode;
        if (response.ContentType is not null)
        {
            context.Response.ContentType = response.ContentType;
        }

        await context.Response.Body.WriteAsync(response.Body, cancellationToken);
        return Results.Empty;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }
}
