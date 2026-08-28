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
}
