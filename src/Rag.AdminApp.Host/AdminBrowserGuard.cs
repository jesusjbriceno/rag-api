using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc;
using Rag.Infrastructure;

namespace Rag.AdminApp.Host;

/// <summary>
/// Per-browser-request authorization for the BFF. Identity is only ever the verified
/// Cloudflare Access credential: headers that could declare an identity are rejected,
/// and the verified opaque subject must be explicitly allowlisted.
/// </summary>
internal static class AdminBrowserGuard
{
    public static (string Subject, IResult? Problem) Authorize(
        HttpContext context, CloudflareAccessValidator validator, AdminBffOptions options)
    {
        // A browser must never be able to declare its own identity. Reject any of the
        // forbidden identity headers except the ones Cloudflare Access injects on every
        // request reaching this origin: the edge sets them itself, so their presence is
        // not a browser claim. Only the Cloudflare credential is trusted, and the proxy
        // never forwards any of these headers upstream.
        if (context.Request.Headers.Keys.Any(AdminIdentityHeaderPolicy.IsForbiddenFromBrowser))
        {
            return (string.Empty, AdminBffProblems.Create(StatusCodes.Status400BadRequest, "forbidden_identity_header"));
        }

        var subject = validator.Validate(context.Request.Headers["Cf-Access-Jwt-Assertion"].ToString());
        if (subject is null)
        {
            return (string.Empty, AdminBffProblems.Create(StatusCodes.Status401Unauthorized, "unauthorized"));
        }

        if (!options.AllowedSubjects.Contains(subject, StringComparer.Ordinal))
        {
            return (string.Empty, AdminBffProblems.Create(StatusCodes.Status403Forbidden, "not_allowlisted"));
        }

        return (subject, null);
    }

    /// <summary>
    /// Same-origin/antiforgery protection for mutation routes: browsers always send an
    /// Origin header on cross-site POSTs, so a missing or non-matching Origin fails
    /// closed before any upstream request is built.
    /// </summary>
    public static IResult? RequireSameOrigin(HttpContext context, AdminBffOptions options)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin) ||
            !string.Equals(origin.TrimEnd('/'), options.AllowedOrigin!.TrimEnd('/'), StringComparison.Ordinal))
        {
            return AdminBffProblems.Create(StatusCodes.Status403Forbidden, "cross_origin_rejected");
        }

        return null;
    }
}

internal static class AdminBffProblems
{
    public static IResult Create(int statusCode, string code) =>
        Results.Problem(new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode),
            Extensions = { ["code"] = code },
        });
}
