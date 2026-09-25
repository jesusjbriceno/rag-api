using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Rag.Infrastructure;

namespace Rag.Api;

// Emits a single structured, secret-free log record per admin request. It reads
// only the HTTP route (no query string), response status, elapsed time, verified
// actor claims, the auth-failure reason category, and the problem code set by
// AdminProblem — never headers, assertions, bodies, secrets, hashes, or salts.
public sealed class AdminObservabilityMiddleware(
    RequestDelegate next,
    ILogger<AdminObservabilityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/admin"))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            Emit(context, stopwatch.ElapsedMilliseconds);
        }
    }

    private void Emit(HttpContext context, long latencyMs)
    {
        var routePath = context.Request.Path.Value;
        var status = context.Response.StatusCode;
        var appId = context.User.FindFirst(AdminAuthenticationDefaults.AppIdClaim)?.Value;
        var actorSubject = context.User.FindFirst(AdminAuthenticationDefaults.ActorSubjectClaim)?.Value;
        var failureReason = ReadReason(context);
        var problemCode = ReadString(context, AdminObservabilityKeys.ProblemCode);

        logger.LogInformation(
            "Admin request {RoutePath} completed with status {Status} in {LatencyMs} ms; app {AppId}; actor {ActorSubject}; reason {FailureReason}; code {ProblemCode}.",
            routePath,
            status,
            latencyMs,
            appId ?? string.Empty,
            actorSubject ?? string.Empty,
            failureReason ?? string.Empty,
            problemCode ?? string.Empty);
    }

    private static string? ReadReason(HttpContext context) =>
        context.Items.TryGetValue(AdminObservabilityKeys.AuthFailureReason, out var value) &&
        value is AdminAuthFailureReason reason && reason != AdminAuthFailureReason.None
            ? ToSnakeCase(reason.ToString())
            : null;

    private static string? ReadString(HttpContext context, string key) =>
        context.Items.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (var character in value)
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
