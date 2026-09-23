using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.Api;

public static class AdminAuthenticationDefaults
{
    public const string AuthenticationScheme = "Admin";

    // Header names are aliased to the shared Rag.Infrastructure wire contract so the
    // AdminApp BFF and the API cannot drift. Preserved here for compatibility.
    public const string AppIdHeader = AdminAuthenticationContract.AppIdHeader;
    public const string KeyIdHeader = AdminAuthenticationContract.KeyIdHeader;
    public const string TimestampHeader = AdminAuthenticationContract.TimestampHeader;
    public const string SignatureHeader = AdminAuthenticationContract.SignatureHeader;
    public const string AssertionHeader = AdminAuthenticationContract.AssertionHeader;
    public const string IdempotencyKeyHeader = AdminAuthenticationContract.IdempotencyKeyHeader;

    public const string ActorSubjectClaim = "admin_actor_subject";
    public const string AppIdClaim = "admin_app_id";
    public const string IdempotencyKeyClaim = "admin_idempotency_key";
    public const string RequestFingerprintClaim = "admin_request_fingerprint";
}

public sealed class AdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Keys.Any(AdminIdentityHeaderPolicy.IsForbidden))
        {
            Context.Items[AdminObservabilityKeys.AuthFailureReason] = AdminAuthFailureReason.ForbiddenIdentityHeader;
            return AuthenticateResult.Fail("Forbidden identity header present.");
        }

        var appId = ReadSingleHeader(AdminAuthenticationDefaults.AppIdHeader);
        var keyId = ReadSingleHeader(AdminAuthenticationDefaults.KeyIdHeader);
        var timestampRaw = ReadSingleHeader(AdminAuthenticationDefaults.TimestampHeader);
        var signature = ReadSingleHeader(AdminAuthenticationDefaults.SignatureHeader);
        var assertionJws = ReadSingleHeader(AdminAuthenticationDefaults.AssertionHeader);
        var idempotencyKey = ReadSingleHeader(AdminAuthenticationDefaults.IdempotencyKeyHeader);

        if (appId is null || keyId is null || timestampRaw is null || signature is null ||
            assertionJws is null || idempotencyKey is null ||
            !long.TryParse(timestampRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var timestampUnixSeconds))
        {
            Context.Items[AdminObservabilityKeys.AuthFailureReason] = AdminAuthFailureReason.IncompleteHeaders;
            return AuthenticateResult.Fail("Admin authentication headers are incomplete.");
        }

        var bodyBytes = await ReadBodyAsync();
        if (bodyBytes is null)
        {
            Context.Items[AdminObservabilityKeys.AuthFailureReason] = AdminAuthFailureReason.BodyTooLarge;
            return AuthenticateResult.Fail("Admin request body is too large.");
        }

        var proof = new AdminMachineProof(
            Request.Method,
            Request.Path.ToString() + Request.QueryString.ToString(),
            ComputeSha256Hex(bodyBytes),
            ComputeSha256Hex(Encoding.UTF8.GetBytes(assertionJws)),
            appId,
            keyId,
            timestampUnixSeconds,
            idempotencyKey);

        var authenticator = Context.RequestServices.GetRequiredService<AdminAuthenticator>();
        var result = await authenticator.AuthenticateAsync(
            proof,
            signature,
            assertionJws,
            DateTimeOffset.UtcNow,
            Context.RequestAborted);

        if (!result.Succeeded)
        {
            Context.Items[AdminObservabilityKeys.AuthFailureReason] = result.FailureReason;
            return AuthenticateResult.Fail("Admin authentication failed.");
        }

        var actor = result.Actor!.Value;
        var fingerprint = ComputeSha256Hex(Encoding.UTF8.GetBytes(
            $"{Request.Method}\n{Request.Path.ToString()}{Request.QueryString.ToString()}\n{proof.BodyHash}"));

        var identity = new ClaimsIdentity(
            [
                new Claim(AdminAuthenticationDefaults.ActorSubjectClaim, actor.ActorSubject),
                new Claim(AdminAuthenticationDefaults.AppIdClaim, actor.AppId),
                new Claim(AdminAuthenticationDefaults.IdempotencyKeyClaim, idempotencyKey),
                new Claim(AdminAuthenticationDefaults.RequestFingerprintClaim, fingerprint),
            ],
            Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await AdminProblem.Create(
            Context,
            StatusCodes.Status401Unauthorized,
            "Unauthorized",
            "unauthorized").ExecuteAsync(Context);
    }

    private string? ReadSingleHeader(string name)
    {
        if (Request.Headers.TryGetValue(name, out var values) &&
            values.Count == 1 &&
            !string.IsNullOrWhiteSpace(values[0]))
        {
            return values[0];
        }

        return null;
    }

    private async Task<byte[]?> ReadBodyAsync()
    {
        Request.EnableBuffering();
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, Context.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > AdminAuthenticationContract.MaxBodyBytes)
            {
                return null;
            }
        }

        Request.Body.Position = 0;
        return buffer.ToArray();
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public static class AdminHttpContextExtensions
{
    public static AdminActor? GetAdminActor(this HttpContext context)
    {
        var subject = context.User.FindFirst(AdminAuthenticationDefaults.ActorSubjectClaim)?.Value;
        var appId = context.User.FindFirst(AdminAuthenticationDefaults.AppIdClaim)?.Value;
        return subject is not null && appId is not null ? new AdminActor(subject, appId) : null;
    }

    public static Guid? GetAdminIdempotencyKey(this HttpContext context) =>
        Guid.TryParse(context.User.FindFirst(AdminAuthenticationDefaults.IdempotencyKeyClaim)?.Value, out var key)
            ? key
            : null;

    public static string? GetAdminRequestFingerprint(this HttpContext context) =>
        context.User.FindFirst(AdminAuthenticationDefaults.RequestFingerprintClaim)?.Value;
}
