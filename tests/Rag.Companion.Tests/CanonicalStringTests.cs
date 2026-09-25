using Rag.Companion.Protocol;

namespace Rag.Companion.Tests;

public sealed class CanonicalStringTests
{
    private const string Secret = "s3cret-value-1234567890abcdef";
    private const string CompanionId = "companion-0001";
    private const string KeyId = "key-0001";
    private const string BodySha256 = "f1e39d9b6cd3a60db72c0b80e4679e715bd7e55c1a22762e96234045f46da55c";
    private const string Nonce = "7f3c9a1e2b4d5f6a8c0e1d2f3a4b5c6d";

    [Fact]
    public void Body_sha256_matches_known_answer()
    {
        Assert.Equal(BodySha256, CompanionSigner.ComputeBodySha256("{\"protocolVersion\":1}"));
    }

    [Fact]
    public void Body_sha256_is_lowercase_hex()
    {
        var hash = CompanionSigner.ComputeBodySha256("hello");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void Canonical_string_joins_seven_lines_in_fixed_order()
    {
        var canonical = CanonicalString.Create("POST", "/companion/v1/import-jobs/lease", BodySha256, CompanionId, KeyId, 1735689600, Nonce);

        Assert.Equal(
            "POST\n/companion/v1/import-jobs/lease\n" + BodySha256 + "\n" + CompanionId + "\n" + KeyId + "\n1735689600\n" + Nonce,
            canonical.ToString());
    }

    [Fact]
    public void Lease_signature_matches_known_answer()
    {
        var canonical = CanonicalString.Create("POST", "/companion/v1/import-jobs/lease", BodySha256, CompanionId, KeyId, 1735689600, Nonce);

        Assert.Equal("BP4lG8kNHBdi5KJmbz6Birv6fT4FkVi5+Sv9LTbI1SM=", CompanionSigner.ComputeSignature(Secret, canonical.ToString()));
    }

    [Fact]
    public void Events_signature_matches_known_answer()
    {
        var canonical = CanonicalString.Create(
            "POST",
            "/companion/v1/import-jobs/job-0001/events",
            "fa79e73f4ac910c37fd128c9b85139af5b9c6145d43fee39583d6234272c24df",
            CompanionId,
            KeyId,
            1735689601,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.Equal("SwoP0P4KA1bu4VWbtTGGVR2Qc6GlFeEVaqlGx7W60cE=", CompanionSigner.ComputeSignature(Secret, canonical.ToString()));
    }

    [Fact]
    public void Tampering_with_any_field_changes_signature()
    {
        var original = CompanionSigner.ComputeSignature(Secret, CanonicalString.Create("POST", "/companion/v1/import-jobs/lease", BodySha256, CompanionId, KeyId, 1735689600, Nonce).ToString());
        var tamperedId = CompanionSigner.ComputeSignature(Secret, CanonicalString.Create("POST", "/companion/v1/import-jobs/lease", BodySha256, "companion-0002", KeyId, 1735689600, Nonce).ToString());
        var tamperedTs = CompanionSigner.ComputeSignature(Secret, CanonicalString.Create("POST", "/companion/v1/import-jobs/lease", BodySha256, CompanionId, KeyId, 1735689601, Nonce).ToString());

        Assert.NotEqual(original, tamperedId);
        Assert.NotEqual(original, tamperedTs);
    }

    [Fact]
    public void Timestamp_is_rendered_as_invariant_integer_seconds()
    {
        Assert.Equal("1735689600", CanonicalString.Create("POST", "/p", "a", "c", "k", 1735689600, "n").Timestamp);
    }
}
