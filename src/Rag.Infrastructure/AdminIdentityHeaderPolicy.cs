namespace Rag.Infrastructure;

public static class AdminIdentityHeaderPolicy
{
    public static readonly IReadOnlySet<string> ForbiddenHeaders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cf-Access-Jwt-Assertion",
            "Cf-Access-Authenticated-User-Email",
            "Cf-Access-Authenticated-User-Groups",
            "X-Forwarded-User",
            "X-Forwarded-Email",
            "X-Forwarded-Groups",
            "X-Forwarded-Preferred-Username",
            "X-Auth-Request-User",
            "X-Auth-Request-Email",
            "X-Auth-Request-Groups",
            "X-Authenticated-User",
            "X-Remote-User",
            "X-WebAuth-User",
        };

    public static bool IsForbidden(string headerName) => ForbiddenHeaders.Contains(headerName);

    /// <summary>
    /// Headers Cloudflare Access injects on every request that reaches the origin. The BFF
    /// consumes the credential and ignores the rest, so they are not a browser-supplied
    /// identity claim and must not be treated as an attack on their own.
    /// </summary>
    public static readonly IReadOnlySet<string> EdgeInjectedHeaders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cf-Access-Jwt-Assertion",
            "Cf-Access-Authenticated-User-Email",
            "Cf-Access-Authenticated-User-Groups",
        };

    /// <summary>
    /// True for identity headers a browser-facing request must never carry. Edge-injected
    /// headers are excluded because the edge adds them before the request reaches us.
    /// </summary>
    public static bool IsForbiddenFromBrowser(string headerName) =>
        ForbiddenHeaders.Contains(headerName) && !EdgeInjectedHeaders.Contains(headerName);
}
