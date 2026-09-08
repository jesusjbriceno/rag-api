using System.Security.Cryptography;
using System.Text;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminMachineProofVerifierTests
{
    private const string AppId = "admin-app";
    private const string KeyId = "machine-key-1";
    private const string CurrentSecret = "current-secret";
    private const string PreviousSecret = "previous-secret";

    [Fact]
    public void Valid_proof_signed_with_the_current_secret_is_accepted()
    {
        var options = CreateOptions(CurrentSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        Assert.True(verifier.Verify(proof, Sign(proof, CurrentSecret), now));
    }

    [Fact]
    public void Forged_proof_signed_with_a_different_secret_is_rejected()
    {
        var options = CreateOptions(CurrentSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        Assert.False(verifier.Verify(proof, Sign(proof, "attacker-secret"), now));
    }

    [Fact]
    public void Stale_proof_with_a_timestamp_outside_the_clock_skew_is_rejected()
    {
        var options = CreateOptions(CurrentSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;

        var tooOld = CreateProof(now.AddSeconds(-120));
        Assert.False(verifier.Verify(tooOld, Sign(tooOld, CurrentSecret), now));

        var tooNew = CreateProof(now.AddSeconds(120));
        Assert.False(verifier.Verify(tooNew, Sign(tooNew, CurrentSecret), now));
    }

    [Fact]
    public void Proof_with_an_out_of_range_timestamp_is_rejected()
    {
        var options = CreateOptions(CurrentSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now) with { TimestampUnixSeconds = long.MaxValue };

        Assert.False(verifier.Verify(proof, Sign(proof, CurrentSecret), now));
    }

    [Fact]
    public void Proof_signed_with_the_previous_secret_is_accepted_during_rotation()
    {
        var options = CreateOptions(CurrentSecret, PreviousSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        Assert.True(verifier.Verify(proof, Sign(proof, PreviousSecret), now));
    }

    [Fact]
    public void Proof_with_an_unknown_key_id_is_rejected()
    {
        var options = CreateOptions(CurrentSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now) with { KeyId = "unknown-key" };

        Assert.False(verifier.Verify(proof, Sign(proof, CurrentSecret), now));
    }

    [Fact]
    public void Proof_whose_app_id_does_not_match_the_key_is_rejected()
    {
        var options = CreateOptions(CurrentSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now) with { AppId = "other-app" };

        Assert.False(verifier.Verify(proof, Sign(proof, CurrentSecret), now));
    }

    [Fact]
    public void Tampering_with_any_canonical_field_invalidates_the_proof()
    {
        var options = CreateOptions(CurrentSecret);
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);
        var signature = Sign(proof, CurrentSecret);

        Assert.False(verifier.Verify(proof with { Method = "GET" }, signature, now));
        Assert.False(verifier.Verify(proof with { PathAndQuery = "/api/v1/admin/clients/other" }, signature, now));
        Assert.False(verifier.Verify(proof with { BodyHash = "0".PadLeft(64, '0') }, signature, now));
        Assert.False(verifier.Verify(proof with { AssertionHash = "0".PadLeft(64, '0') }, signature, now));
        Assert.False(verifier.Verify(proof with { AppId = "other-app" }, signature, now));
        Assert.False(verifier.Verify(proof with { KeyId = "other-key" }, signature, now));
        Assert.False(verifier.Verify(proof with { TimestampUnixSeconds = proof.TimestampUnixSeconds + 1 }, signature, now));
        Assert.False(verifier.Verify(proof with { IdempotencyKey = "other-idempotency" }, signature, now));
    }

    [Fact]
    public void Canonicalization_normalizes_method_case_and_hash_case()
    {
        var reference = new AdminMachineProof(
            "POST",
            "/api/v1/admin/clients?cursor=abc",
            "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
            "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210",
            AppId,
            KeyId,
            1_700_000_000,
            "idem-1");

        var mixed = reference with
        {
            Method = "post",
            BodyHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            AssertionHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
        };

        Assert.Equal(
            AdminMachineProofVerifier.Canonicalize(reference),
            AdminMachineProofVerifier.Canonicalize(mixed));
    }

    [Fact]
    public void App_auth_options_reject_invalid_configuration()
    {
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions().Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = "", KeyId = KeyId, CurrentSecret = CurrentSecret }],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = "", CurrentSecret = CurrentSecret }],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = "" }],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions
        {
            Apps =
            [
                new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = CurrentSecret },
                new AdminAppOptions { AppId = AppId, KeyId = "key-2", CurrentSecret = CurrentSecret },
            ],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions
        {
            Apps =
            [
                new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = CurrentSecret },
                new AdminAppOptions { AppId = "app-2", KeyId = KeyId, CurrentSecret = CurrentSecret },
            ],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = CurrentSecret, PreviousSecret = CurrentSecret }],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = CurrentSecret }],
            ClockSkewSeconds = -1,
        }.Validate());
    }

    private static AdminAppAuthOptions CreateOptions(string currentSecret, string? previousSecret = null) => new()
    {
        Apps =
        [
            new AdminAppOptions
            {
                AppId = AppId,
                KeyId = KeyId,
                CurrentSecret = currentSecret,
                PreviousSecret = previousSecret,
            },
        ],
    };

    private static AdminMachineProof CreateProof(DateTimeOffset now) => new(
        "POST",
        "/api/v1/admin/clients",
        "5f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f",
        "3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f",
        AppId,
        KeyId,
        now.ToUnixTimeSeconds(),
        "idem-1");

    private static string Sign(AdminMachineProof proof, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(AdminMachineProofVerifier.Canonicalize(proof)));
        return Convert.ToBase64String(digest);
    }
}
