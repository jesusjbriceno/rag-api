using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public JwtPrivateKeyOptions CurrentSigningKey { get; set; } = new();

    public List<JwtPublicKeyOptions> ValidationKeys { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("Jwt issuer and audience must be configured.");
        }

        if (!IsValidKeyId(CurrentSigningKey.KeyId) || string.IsNullOrWhiteSpace(CurrentSigningKey.PrivateKeyPem))
        {
            throw new InvalidOperationException("The current JWT signing key id and private key must be configured.");
        }

        if (ValidationKeys.Count == 0 || ValidationKeys.Any(key => !IsValidKeyId(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyPem)))
        {
            throw new InvalidOperationException("At least one valid JWT public validation key must be configured.");
        }

        if (ValidationKeys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != ValidationKeys.Count ||
            !ValidationKeys.Any(key => key.KeyId == CurrentSigningKey.KeyId))
        {
            throw new InvalidOperationException("JWT validation keys must be unique and include the current signing key id.");
        }
    }

    private static bool IsValidKeyId(string? keyId) =>
        !string.IsNullOrWhiteSpace(keyId) && keyId.Length <= 200;
}

public sealed class JwtPrivateKeyOptions
{
    public string? KeyId { get; set; }

    public string? PrivateKeyPem { get; set; }
}

public sealed class JwtPublicKeyOptions
{
    public string? KeyId { get; set; }

    public string? PublicKeyPem { get; set; }
}

public sealed class JwtKeyMaterial : IDisposable
{
    public JwtKeyMaterial(JwtOptions options)
    {
        options.Validate();
        try
        {
            CurrentPrivateKey = RSA.Create();
            CurrentPrivateKey.ImportFromPem(options.CurrentSigningKey.PrivateKeyPem!);
            EnsurePrivateKey(CurrentPrivateKey);
            CurrentSigningKey = new RsaSecurityKey(CurrentPrivateKey) { KeyId = options.CurrentSigningKey.KeyId };
            ValidationKeys = options.ValidationKeys.ToDictionary(
                key => key.KeyId!,
                key => CreateValidationKey(key),
                StringComparer.Ordinal);
            if (!ValidationKeys.TryGetValue(CurrentSigningKey.KeyId!, out var currentPublicKey) ||
                !PublicKeysMatch(CurrentPrivateKey, currentPublicKey.Rsa!))
            {
                throw new InvalidOperationException("The current JWT private key does not match its configured validation public key.");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or InvalidOperationException)
        {
            Dispose();
            throw new InvalidOperationException("JWT key material is invalid.", exception);
        }
    }

    public RSA CurrentPrivateKey { get; private set; } = null!;

    public RsaSecurityKey CurrentSigningKey { get; private set; } = null!;

    public IReadOnlyDictionary<string, RsaSecurityKey> ValidationKeys { get; private set; } = null!;

    public void Dispose()
    {
        CurrentPrivateKey?.Dispose();
        foreach (var key in ValidationKeys?.Values ?? [])
        {
            key.Rsa?.Dispose();
        }
    }

    private static RsaSecurityKey CreateValidationKey(JwtPublicKeyOptions options)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(options.PublicKeyPem!);
        return new RsaSecurityKey(rsa) { KeyId = options.KeyId };
    }

    private static void EnsurePrivateKey(RSA key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: true);
        if (parameters.D is null || parameters.P is null || parameters.Q is null ||
            parameters.DP is null || parameters.DQ is null || parameters.InverseQ is null)
        {
            throw new InvalidOperationException("The current JWT signing key does not contain private RSA parameters.");
        }
    }

    private static bool PublicKeysMatch(RSA privateKey, RSA publicKey)
    {
        var privateParameters = privateKey.ExportParameters(false);
        var publicParameters = publicKey.ExportParameters(false);
        return privateParameters.Modulus.AsSpan().SequenceEqual(publicParameters.Modulus) &&
            privateParameters.Exponent.AsSpan().SequenceEqual(publicParameters.Exponent);
    }
}

public sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options, JwtKeyMaterial keyMaterial) : IAccessTokenIssuer
{
    public AccessToken Issue(ClientCredential credential, DateTimeOffset now)
    {
        var expiresAt = now.AddMinutes(15);
        var token = new JwtSecurityToken(
            options.Value.Issuer,
            options.Value.Audience,
            [
                new Claim("client_id", credential.ServiceClientId.ToString("D")),
                new Claim("credential_id", credential.Id.ToString("D")),
                new Claim("credential_version", credential.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            now.UtcDateTime,
            expiresAt.UtcDateTime,
            new SigningCredentials(keyMaterial.CurrentSigningKey, SecurityAlgorithms.RsaSha256));
        token.Header[JwtHeaderParameterNames.Kid] = keyMaterial.CurrentSigningKey.KeyId;
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
