using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Rag.AdminApp;

public sealed record CloudflareAccessKey(string KeyId, string PublicKeyPem);

public sealed record CloudflareAccessOptions(string Issuer, string Audience, IReadOnlyList<CloudflareAccessKey> Keys);

/// <summary>
/// Validates a Cloudflare Access credential (a short-lived RS256 JWT issued by the team
/// domain) and returns the verified opaque subject, or null when the credential is
/// absent, expired, or fails cryptographic validation. No identity is forwarded on failure.
/// </summary>
public sealed class CloudflareAccessValidator
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };
    private readonly TokenValidationParameters _parameters;
    private readonly Dictionary<string, RsaSecurityKey> _keys = new(StringComparer.Ordinal);

    public CloudflareAccessValidator(CloudflareAccessOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new ArgumentException("Cloudflare Access issuer and audience must be configured.", nameof(options));
        }

        if (options.Keys.Count == 0)
        {
            throw new ArgumentException("At least one Cloudflare Access validation key must be configured.", nameof(options));
        }

        foreach (var key in options.Keys)
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
            _keys.Add(key.KeyId, new RsaSecurityKey(rsa) { KeyId = key.KeyId });
        }

        _parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = ClockSkew,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeyResolver = (_, _, kid, _) =>
                kid is not null && _keys.TryGetValue(kid, out var key) ? [key] : [],
        };
    }

    public string? Validate(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return null;
        }

        try
        {
            var principal = _handler.ValidateToken(credential, _parameters, out _);
            var subject = principal.FindFirst("sub")?.Value;
            return string.IsNullOrWhiteSpace(subject) ? null : subject;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }
}
