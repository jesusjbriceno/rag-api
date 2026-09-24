using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class AdminPlaneOptions
{
    public const string SectionName = "AdminPlane";

    public bool Enabled { get; set; }
}

public sealed class AdminAssertionOptions
{
    public const string SectionName = "AdminAssertion";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public List<AdminAssertionValidationKeyOptions> ValidationKeys { get; set; } = [];

    public int LifetimeSeconds { get; set; } = 60;

    public int ClockSkewSeconds { get; set; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("Admin assertion issuer and audience must be configured.");
        }

        if (ValidationKeys.Count == 0 || ValidationKeys.Any(key => string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyPem)))
        {
            throw new InvalidOperationException("At least one valid admin assertion validation key must be configured.");
        }

        if (ValidationKeys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != ValidationKeys.Count)
        {
            throw new InvalidOperationException("Admin assertion validation key ids must be unique.");
        }

        if (LifetimeSeconds <= 0 || ClockSkewSeconds < 0)
        {
            throw new InvalidOperationException("Admin assertion lifetime and clock skew must be valid.");
        }
    }
}

public sealed class AdminAssertionValidationKeyOptions
{
    public string? KeyId { get; set; }

    public string? PublicKeyPem { get; set; }
}

public sealed class AdminAppAuthOptions
{
    public const string SectionName = "AdminAppAuth";

    public List<AdminAppOptions> Apps { get; set; } = [];

    public int ClockSkewSeconds { get; set; } = 60;

    public void Validate()
    {
        if (Apps.Count == 0 || Apps.Any(app => string.IsNullOrWhiteSpace(app.AppId) || string.IsNullOrWhiteSpace(app.KeyId) || string.IsNullOrWhiteSpace(app.Secret)))
        {
            throw new InvalidOperationException("At least one admin app with an app id, key id, and secret must be configured.");
        }

        if (Apps.Select(app => app.AppId).Distinct(StringComparer.Ordinal).Count() != Apps.Count)
        {
            throw new InvalidOperationException("Admin app ids must be unique.");
        }

        if (Apps.Any(app => string.Equals(app.Secret, app.PreviousSecret, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("An admin app previous secret must differ from its current secret.");
        }

        if (ClockSkewSeconds < 0)
        {
            throw new InvalidOperationException("Admin app auth clock skew must be non-negative.");
        }
    }
}

public sealed class AdminAppOptions
{
    public string? AppId { get; set; }

    public string? KeyId { get; set; }

    public string? Secret { get; set; }

    public string? PreviousSecret { get; set; }
}

public sealed class AdminAssertionKeyRing : IDisposable
{
    private Dictionary<string, RsaSecurityKey> _keys = null!;

    public AdminAssertionKeyRing(AdminAssertionOptions options)
    {
        options.Validate();
        var keys = new Dictionary<string, RsaSecurityKey>(StringComparer.Ordinal);
        try
        {
            foreach (var item in options.ValidationKeys)
            {
                keys.Add(item.KeyId!, CreateValidationKey(item));
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            foreach (var key in keys.Values)
            {
                key.Rsa?.Dispose();
            }

            throw new InvalidOperationException("Admin assertion validation key material is invalid.", exception);
        }

        _keys = keys;
    }

    public bool TryGetKey(string? keyId, out RsaSecurityKey key)
    {
        if (keyId is not null && _keys.TryGetValue(keyId, out var found))
        {
            key = found;
            return true;
        }

        key = null!;
        return false;
    }

    public void Remove(string keyId)
    {
        if (_keys.Remove(keyId, out var key))
        {
            key.Rsa?.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Rsa?.Dispose();
        }
    }

    private static RsaSecurityKey CreateValidationKey(AdminAssertionValidationKeyOptions options)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(options.PublicKeyPem!);
        return new RsaSecurityKey(rsa) { KeyId = options.KeyId };
    }
}

public sealed record AdminAssertion(string Subject, string AppId, string Issuer, string Jti, DateTimeOffset ExpiresAt);

public sealed record AdminMachineProof(string? AppId, string? KeyId, string? Timestamp, string? IdempotencyKey, string? Signature);

public sealed class AdminAssertionValidator(AdminAssertionOptions options, AdminAssertionKeyRing keyRing)
{
    private readonly System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public AdminAssertion? Validate(string jws, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(jws))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeyResolver = (_, _, kid, _) => keyRing.TryGetKey(kid, out var key) ? [key] : [],
        };

        try
        {
            var principal = _handler.ValidateToken(jws, parameters, out var validatedToken);
            var subject = principal.FindFirst("sub")?.Value;
            var appId = principal.FindFirst("app_id")?.Value;
            var jti = principal.FindFirst("jti")?.Value;
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(jti))
            {
                return null;
            }

            if (!TryReadEpoch(principal.FindFirst("iat")?.Value, out var issuedAtUnix) ||
                !TryReadEpoch(principal.FindFirst("nbf")?.Value, out _) ||
                !TryReadEpoch(principal.FindFirst("exp")?.Value, out var expiresAtUnix))
            {
                return null;
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtUnix);
            if (issuedAt > now.AddSeconds(options.ClockSkewSeconds))
            {
                return null;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix);
            if (expiresAt - issuedAt > TimeSpan.FromSeconds(options.LifetimeSeconds))
            {
                return null;
            }

            return new AdminAssertion(subject, appId, options.Issuer!, jti, expiresAt);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryReadEpoch(string? value, out long epoch)
    {
        epoch = 0;
        return value is not null &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out epoch);
    }
}

public sealed class AdminMachineProofVerifier(AdminAppAuthOptions options)
{
    public bool Verify(AdminMachineProof proof, string method, string pathAndQuery, byte[] body, string assertion, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(proof.AppId) || string.IsNullOrWhiteSpace(proof.KeyId) ||
            string.IsNullOrWhiteSpace(proof.Timestamp) || string.IsNullOrWhiteSpace(proof.Signature))
        {
            return false;
        }

        var app = options.Apps.FirstOrDefault(candidate => string.Equals(candidate.AppId, proof.AppId, StringComparison.Ordinal));
        if (app is null || !string.Equals(app.KeyId, proof.KeyId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(proof.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
            Math.Abs(now.ToUnixTimeSeconds() - timestamp) > options.ClockSkewSeconds)
        {
            return false;
        }

        var canonical = Canonicalize(method, pathAndQuery, body, assertion, proof.AppId!, proof.KeyId!, proof.Timestamp!, proof.IdempotencyKey);
        if (FixedTimeEquals(proof.Signature!, ComputeSignature(app.Secret!, canonical)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(app.PreviousSecret) &&
            FixedTimeEquals(proof.Signature!, ComputeSignature(app.PreviousSecret, canonical));
    }

    public static string Canonicalize(string method, string pathAndQuery, byte[] body, string assertion, string appId, string keyId, string timestamp, string? idempotencyKey) =>
        string.Join('\n',
        [
            method.ToUpperInvariant(),
            pathAndQuery,
            HashHex(body),
            HashHex(Encoding.UTF8.GetBytes(assertion)),
            appId,
            keyId,
            timestamp,
            idempotencyKey ?? string.Empty,
        ]);

    private static string ComputeSignature(string secret, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string HashHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static bool FixedTimeEquals(string provided, string expected)
    {
        if (provided.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(provided),
            Encoding.ASCII.GetBytes(expected));
    }
}

public sealed class AdminAuthenticator(
    AdminAssertionValidator assertions,
    AdminMachineProofVerifier machineProofs,
    IAdminAssertionReplayRepository replays)
{
    public async Task<AdminActor?> AuthenticateAsync(
        AdminMachineProof proof,
        string assertion,
        string method,
        string pathAndQuery,
        byte[] body,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!machineProofs.Verify(proof, method, pathAndQuery, body, assertion, now))
        {
            return null;
        }

        var validated = assertions.Validate(assertion, now);
        if (validated is null)
        {
            return null;
        }

        if (!string.Equals(proof.AppId, validated.AppId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!await replays.TryReserveAsync(validated.Issuer, validated.Jti, validated.AppId, validated.ExpiresAt, cancellationToken))
        {
            return null;
        }

        return new AdminActor(validated.Subject, validated.AppId);
    }
}

public static class AdminIdentityHeaderPolicy
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "cf-access-jwt-assertion",
        "cf-access-authenticated-user-email",
        "cf-access-authenticated-user-id",
        "cf-access-authenticated-user-groups",
        "x-forwarded-user",
        "x-forwarded-email",
        "x-forwarded-groups",
        "x-auth-request-user",
        "x-auth-request-email",
    };

    public static bool IsForbiddenIdentityHeader(string headerName) => ForbiddenHeaders.Contains(headerName);
}
