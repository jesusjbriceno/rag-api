using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Rag.Infrastructure;

public sealed record AdminAssertion(string Subject, string AppId, string Issuer, string Jti, DateTimeOffset ExpiresAt);

public sealed class AdminAssertionValidator(AdminAssertionOptions options, AdminAssertionKeyRing keyRing)
{
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

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
            var principal = _handler.ValidateToken(jws, parameters, out _);
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
