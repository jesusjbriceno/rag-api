using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Rag.AdminApp;

public sealed record AdminAssertionIssuerOptions(string Issuer, string Audience, string AppId, string KeyId, string PrivateKeyPem);

/// <summary>
/// Issues a short-lived RS256 internal assertion for an admin API attempt after the
/// Cloudflare Access credential has been validated. The assertion carries a non-empty
/// verified subject, issuer, audience, expiry, and a unique replay identifier.
/// </summary>
public sealed class AdminAssertionIssuer
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _appId;
    private readonly SigningCredentials _credentials;

    public AdminAssertionIssuer(AdminAssertionIssuerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            throw new ArgumentException("Assertion app id and signing key must be configured.", nameof(options));
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(options.PrivateKeyPem);
        _credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = options.KeyId }, SecurityAlgorithms.RsaSha256);
        _issuer = options.Issuer;
        _audience = options.Audience;
        _appId = options.AppId;
    }

    public string Issue(string subject, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("An assertion subject is required.", nameof(subject));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = _audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(Lifetime).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["app_id"] = _appId,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = _credentials,
        };
        return _handler.WriteToken(_handler.CreateToken(descriptor));
    }
}
