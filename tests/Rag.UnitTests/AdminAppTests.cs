using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Rag.AdminApp;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAppTests
{
    private const string TeamIssuer = "https://team.cloudflareaccess.com";
    private const string CloudflareAud = "cf-app-aud";
    private const string Subject = "cf-subject-123";
    private const string AppId = "admin-app";

    private static (RSA Rsa, string PrivatePem, string PublicPem) NewKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa, rsa.ExportRSAPrivateKeyPem(), rsa.ExportRSAPublicKeyPem());
    }

    [Fact]
    public void Valid_cloudflare_credential_yields_the_verified_subject()
    {
        using var cf = NewKey().Rsa;
        var validator = new CloudflareAccessValidator(new CloudflareAccessOptions(
            TeamIssuer, CloudflareAud, [new CloudflareAccessKey("cf-key", cf.ExportRSAPublicKeyPem())]));
        var credential = CreateCloudflareToken(cf, "cf-key", Subject, TeamIssuer, CloudflareAud, DateTimeOffset.UtcNow);

        Assert.Equal(Subject, validator.Validate(credential));
    }

    [Fact]
    public void Absent_expired_forged_or_wrong_audience_credential_is_rejected()
    {
        using var cf = NewKey().Rsa;
        using var other = NewKey().Rsa;
        var validator = new CloudflareAccessValidator(new CloudflareAccessOptions(
            TeamIssuer, CloudflareAud, [new CloudflareAccessKey("cf-key", cf.ExportRSAPublicKeyPem())]));
        var now = DateTimeOffset.UtcNow;

        Assert.Null(validator.Validate(string.Empty));
        Assert.Null(validator.Validate("not-a-token"));
        Assert.Null(validator.Validate(CreateCloudflareToken(cf, "cf-key", Subject, TeamIssuer, CloudflareAud, now.AddHours(-1))));
        Assert.Null(validator.Validate(CreateCloudflareToken(other, "cf-key", Subject, TeamIssuer, CloudflareAud, now)));
        Assert.Null(validator.Validate(CreateCloudflareToken(cf, "cf-key", Subject, TeamIssuer, "other-aud", now)));
        Assert.Null(validator.Validate(CreateCloudflareToken(cf, "cf-key", string.Empty, TeamIssuer, CloudflareAud, now)));
    }

    [Fact]
    public void Issued_assertion_carries_required_claims_and_is_short_lived()
    {
        using var key = NewKey().Rsa;
        var issuer = new AdminAssertionIssuer(new AdminAssertionIssuerOptions(
            "admin-issuer", "admin-audience", AppId, "assertion-key", key.ExportRSAPrivateKeyPem()));
        var now = DateTimeOffset.UtcNow;

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(issuer.Issue(Subject, now));

        Assert.Equal(Subject, token.Payload["sub"]);
        Assert.Equal(AppId, token.Payload["app_id"]);
        Assert.NotNull(token.Payload["jti"]);
        Assert.Equal("admin-issuer", token.Issuer);
        Assert.Equal("admin-audience", token.Audiences.Single());
        Assert.InRange(token.ValidTo - token.ValidFrom, TimeSpan.Zero, TimeSpan.FromSeconds(61));
    }

    [Fact]
    public void Admin_app_assertion_is_accepted_by_the_api_validator()
    {
        using var key = NewKey().Rsa;
        var issuer = new AdminAssertionIssuer(new AdminAssertionIssuerOptions(
            "admin-issuer", "admin-audience", AppId, "assertion-key", key.ExportRSAPrivateKeyPem()));
        var apiOptions = new AdminAssertionOptions
        {
            Issuer = "admin-issuer",
            Audience = "admin-audience",
            ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = "assertion-key", PublicKeyPem = key.ExportRSAPublicKeyPem() }],
        };
        using var keyRing = new AdminAssertionKeyRing(apiOptions);
        var validator = new AdminAssertionValidator(apiOptions, keyRing);
        var now = DateTimeOffset.UtcNow;

        var result = validator.Validate(issuer.Issue(Subject, now), now);

        Assert.NotNull(result);
        Assert.Equal(Subject, result.Subject);
        Assert.Equal(AppId, result.AppId);
        Assert.Equal("admin-issuer", result.Issuer);
    }

    private static string CreateCloudflareToken(RSA key, string kid, string subject, string issuer, string audience, DateTimeOffset now)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            Claims = new Dictionary<string, object> { ["sub"] = subject },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }
}
