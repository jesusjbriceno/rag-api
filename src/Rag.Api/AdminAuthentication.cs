using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.Api;

public static class AdminAuthenticationDefaults
{
    public const string AuthenticationScheme = "Admin";

    public const string AssertionHeader = "X-Admin-Assertion";

    public const string AppIdHeader = "X-Admin-App-Id";

    public const string KeyIdHeader = "X-Admin-Key-Id";

    public const string TimestampHeader = "X-Admin-Timestamp";

    public const string IdempotencyKeyHeader = "X-Admin-Idempotency-Key";

    public const string SignatureHeader = "X-Admin-Signature";
}

public sealed class AdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminAuthenticator authenticator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string AdminActorItemKey = "AdminActor";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Keys.Any(AdminIdentityHeaderPolicy.IsForbiddenIdentityHeader))
        {
            return AuthenticateResult.NoResult();
        }

        Request.EnableBuffering();
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await Request.Body.CopyToAsync(buffer);
            body = buffer.ToArray();
            Request.Body.Position = 0;
        }

        var proof = new AdminMachineProof(
            Request.Headers[AdminAuthenticationDefaults.AppIdHeader],
            Request.Headers[AdminAuthenticationDefaults.KeyIdHeader],
            Request.Headers[AdminAuthenticationDefaults.TimestampHeader],
            Request.Headers[AdminAuthenticationDefaults.IdempotencyKeyHeader],
            Request.Headers[AdminAuthenticationDefaults.SignatureHeader]);
        var assertion = Request.Headers[AdminAuthenticationDefaults.AssertionHeader].ToString();
        var pathAndQuery = Request.Path.ToString() + Request.QueryString.ToString();

        var actor = await authenticator.AuthenticateAsync(
            proof,
            assertion,
            Request.Method,
            pathAndQuery,
            body,
            DateTimeOffset.UtcNow,
            Context.RequestAborted);
        if (!actor.HasValue)
        {
            return AuthenticateResult.NoResult();
        }

        var resolvedActor = actor.Value;
        var claims = new[]
        {
            new Claim("sub", resolvedActor.ActorSubject),
            new Claim("app_id", resolvedActor.AppId),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        Context.Items[AdminActorItemKey] = resolvedActor;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public static class AdminHttpContextExtensions
{
    public static AdminActor? GetAdminActor(this HttpContext context) =>
        context.Items.TryGetValue(AdminAuthenticationHandler.AdminActorItemKey, out var value) && value is AdminActor actor
            ? actor
            : null;
}
