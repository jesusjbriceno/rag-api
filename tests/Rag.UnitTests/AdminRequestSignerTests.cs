using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Rag.AdminApp;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminRequestSignerTests
{
    private const string Subject = "cf-subject-123";
    private const string AppId = "admin-app";
    private const string KeyId = "machine-key-1";
    private const string MachineSecret = "machine-secret";
    private const string IdempotencyKey = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private static (AdminAssertionIssuer Issuer, RSA Key) NewIssuer()
    {
        var key = RSA.Create(2048);
        var issuer = new AdminAssertionIssuer(new AdminAssertionIssuerOptions(
            "admin-issuer", "admin-audience", AppId, "assertion-key", key.ExportRSAPrivateKeyPem()));
        return (issuer, key);
    }

    private static AdminRequestSigner NewSigner(RSA assertionKey, string? secret = null) =>
        new(new AdminAssertionIssuer(new AdminAssertionIssuerOptions(
                "admin-issuer", "admin-audience", AppId, "assertion-key", assertionKey.ExportRSAPrivateKeyPem())),
            new AdminRequestSignerOptions(AppId, KeyId, secret ?? MachineSecret));

    private static AdminSignableRequest NewRequest(byte[]? body = null) =>
        new("POST", "/api/v1/admin/clients", body ?? Encoding.UTF8.GetBytes("{\"name\":\"acme\"}"));

    [Fact]
    public void Signed_request_is_accepted_by_the_api_machine_proof_verifier()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = NewSigner(assertionKey);
        var body = Encoding.UTF8.GetBytes("{\"name\":\"acme\"}");
        var now = DateTimeOffset.UtcNow;

        var signed = signer.Sign(Subject, new AdminSignableRequest("POST", "/api/v1/admin/clients", body), now, IdempotencyKey);

        var proof = new AdminMachineProof(
            "POST",
            "/api/v1/admin/clients",
            Convert.ToHexStringLower(SHA256.HashData(body)),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signed.AssertionJws))),
            AppId,
            KeyId,
            now.ToUnixTimeSeconds(),
            IdempotencyKey);
        var verifier = new AdminMachineProofVerifier(new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = MachineSecret }],
            ClockSkewSeconds = 60,
        });

        Assert.True(verifier.Verify(proof, signed.Signature, now));
    }

    [Fact]
    public void Signing_covers_method_path_body_assertion_and_idempotency_exactly()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = NewSigner(assertionKey);
        var body = Encoding.UTF8.GetBytes("{\"name\":\"acme\"}");
        var now = DateTimeOffset.UtcNow;

        var signed = signer.Sign(Subject, new AdminSignableRequest("POST", "/api/v1/admin/clients", body), now, IdempotencyKey);

        var verifier = new AdminMachineProofVerifier(new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = MachineSecret }],
            ClockSkewSeconds = 60,
        });

        // Any tampering with a signed component must fail HMAC verification.
        var basis = new AdminMachineProof(
            "POST",
            "/api/v1/admin/clients",
            Convert.ToHexStringLower(SHA256.HashData(body)),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signed.AssertionJws))),
            AppId,
            KeyId,
            now.ToUnixTimeSeconds(),
            IdempotencyKey);

        Assert.True(verifier.Verify(basis, signed.Signature, now));
        Assert.False(verifier.Verify(basis with { Method = "PUT" }, signed.Signature, now));
        Assert.False(verifier.Verify(basis with { PathAndQuery = "/api/v1/admin/clients/other" }, signed.Signature, now));
        Assert.False(verifier.Verify(basis with { BodyHash = Convert.ToHexStringLower(SHA256.HashData("tampered"u8.ToArray())) }, signed.Signature, now));
        Assert.False(verifier.Verify(basis with { AppId = "other-app" }, signed.Signature, now));
        Assert.False(verifier.Verify(basis with { KeyId = "other-key" }, signed.Signature, now));
        Assert.False(verifier.Verify(basis with { IdempotencyKey = Guid.NewGuid().ToString("D") }, signed.Signature, now));
        Assert.False(verifier.Verify(basis, signed.Signature, now.AddMinutes(5)));
    }

    [Fact]
    public void Same_logical_operation_keeps_the_caller_key_and_gets_a_fresh_assertion()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = NewSigner(assertionKey);
        var now = DateTimeOffset.UtcNow;

        var first = signer.Sign(Subject, NewRequest(), now, IdempotencyKey);
        var second = signer.Sign(Subject, NewRequest(), now, IdempotencyKey);

        Assert.NotEqual(first.AssertionJws, second.AssertionJws);
        Assert.Equal(IdempotencyKey, first.IdempotencyKey);
        Assert.Equal(IdempotencyKey, second.IdempotencyKey);
        Assert.NotEqual(first.Signature, second.Signature);
    }

    [Fact]
    public void Caller_idempotency_key_is_bound_verbatim_into_headers_and_hmac_proof()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = NewSigner(assertionKey);
        var now = DateTimeOffset.UtcNow;
        var callerKey = "e902893a-9d22-c4a4-bba5-1a3b2c3d4e5f";

        var signed = signer.Sign(Subject, NewRequest(), now, callerKey);

        Assert.Equal(callerKey, signed.IdempotencyKey);
        Assert.Equal(callerKey, signed.Headers["Idempotency-Key"]);
        var verifier = new AdminMachineProofVerifier(new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = MachineSecret }],
            ClockSkewSeconds = 60,
        });
        var proof = new AdminMachineProof(
            "POST",
            "/api/v1/admin/clients",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("{\"name\":\"acme\"}"))),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signed.AssertionJws))),
            AppId,
            KeyId,
            now.ToUnixTimeSeconds(),
            callerKey);

        // The caller's key is inside the canonical string: the proof verifies with it
        // and fails with any other key.
        Assert.True(verifier.Verify(proof, signed.Signature, now));
        Assert.False(verifier.Verify(proof with { IdempotencyKey = Guid.NewGuid().ToString("D") }, signed.Signature, now));
    }

    [Fact]
    public void Signed_assertion_carries_the_verified_subject_and_app_id_for_the_api_validator()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = NewSigner(assertionKey);
        var now = DateTimeOffset.UtcNow;

        var signed = signer.Sign(Subject, NewRequest(), now, IdempotencyKey);

        var apiOptions = new AdminAssertionOptions
        {
            Issuer = "admin-issuer",
            Audience = "admin-audience",
            ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = "assertion-key", PublicKeyPem = assertionKey.ExportRSAPublicKeyPem() }],
        };
        using var keyRing = new AdminAssertionKeyRing(apiOptions);
        var validator = new AdminAssertionValidator(apiOptions, keyRing);
        var result = validator.Validate(signed.AssertionJws, now);

        Assert.NotNull(result);
        Assert.Equal(Subject, result.Subject);
        Assert.Equal(AppId, result.AppId);
        Assert.InRange((result.ExpiresAt - now).TotalSeconds, 0, 61);
    }

    [Fact]
    public void Signed_request_exposes_the_admin_headers_the_api_requires()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = NewSigner(assertionKey);
        var now = DateTimeOffset.UtcNow;

        var signed = signer.Sign(Subject, NewRequest(), now, IdempotencyKey);

        Assert.Equal(AppId, signed.Headers["X-Admin-App-Id"]);
        Assert.Equal(KeyId, signed.Headers["X-Admin-Key-Id"]);
        Assert.Equal(now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), signed.Headers["X-Admin-Timestamp"]);
        Assert.Equal(signed.Signature, signed.Headers["X-Admin-Signature"]);
        Assert.Equal(signed.AssertionJws, signed.Headers["X-Admin-Assertion"]);
        Assert.Equal(signed.IdempotencyKey, signed.Headers["Idempotency-Key"]);
        Assert.Equal(IdempotencyKey, signed.Headers["Idempotency-Key"]);
        Assert.Equal(6, signed.Headers.Count);
    }

    [Fact]
    public void Empty_subject_or_request_fields_are_rejected()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = NewSigner(assertionKey);

        Assert.Throws<ArgumentException>(() => signer.Sign(string.Empty, NewRequest(), DateTimeOffset.UtcNow, IdempotencyKey));
        Assert.Throws<ArgumentException>(() => signer.Sign(" ", NewRequest(), DateTimeOffset.UtcNow, IdempotencyKey));
        Assert.Throws<ArgumentException>(() => signer.Sign(Subject, NewRequest() with { Method = "" }, DateTimeOffset.UtcNow, IdempotencyKey));
        Assert.Throws<ArgumentException>(() => signer.Sign(Subject, NewRequest() with { PathAndQuery = " " }, DateTimeOffset.UtcNow, IdempotencyKey));
        Assert.Throws<ArgumentException>(() => signer.Sign(Subject, NewRequest() with { Body = null! }, DateTimeOffset.UtcNow, IdempotencyKey));
        Assert.Throws<ArgumentException>(() => signer.Sign(Subject, NewRequest(), DateTimeOffset.UtcNow, string.Empty));
        Assert.Throws<ArgumentException>(() => signer.Sign(Subject, NewRequest(), DateTimeOffset.UtcNow, " "));
    }

    [Fact]
    public void Signer_options_must_be_complete()
    {
        using var assertionKey = RSA.Create(2048);
        Assert.Throws<ArgumentException>(() => NewSigner(assertionKey, secret: ""));
        Assert.Throws<ArgumentNullException>(() => new AdminRequestSigner(null!, new AdminRequestSignerOptions(AppId, KeyId, MachineSecret)));
        Assert.Throws<ArgumentNullException>(() => new AdminRequestSigner(
            new AdminAssertionIssuer(new AdminAssertionIssuerOptions("i", "a", AppId, "k", assertionKey.ExportRSAPrivateKeyPem())), null!));
    }

    [Fact]
    public void Issuer_validation_errors_propagate()
    {
        using var assertionKey = RSA.Create(2048);
        // A signer over a broken assertion issuer cannot silently produce an unsigned request.
        Assert.Throws<ArgumentException>(() => new AdminAssertionIssuer(
            new AdminAssertionIssuerOptions("admin-issuer", "admin-audience", string.Empty, "assertion-key", assertionKey.ExportRSAPrivateKeyPem())));
    }
}
