using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rag.Infrastructure;

public sealed record AdminMachineProof(
    string Method,
    string PathAndQuery,
    string BodyHash,
    string AssertionHash,
    string AppId,
    string KeyId,
    long TimestampUnixSeconds,
    string IdempotencyKey);

public sealed class AdminMachineProofVerifier(AdminAppAuthOptions options)
{
    public bool Verify(AdminMachineProof proof, string signature, DateTimeOffset now)
    {
        if (options.Apps.FirstOrDefault(app => string.Equals(app.KeyId, proof.KeyId, StringComparison.Ordinal)) is not { } app)
        {
            return false;
        }

        if (!string.Equals(proof.AppId, app.AppId, StringComparison.Ordinal))
        {
            return false;
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(proof.TimestampUnixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (Math.Abs((now - timestamp).TotalSeconds) > options.ClockSkewSeconds)
        {
            return false;
        }

        if (!TryDecodeSignature(signature, out var providedSignature))
        {
            return false;
        }

        var canonical = Canonicalize(proof);
        if (ConstantTimeEquals(ComputeHmac(canonical, app.CurrentSecret!), providedSignature))
        {
            return true;
        }

        if (app.PreviousSecret is not null)
        {
            return ConstantTimeEquals(ComputeHmac(canonical, app.PreviousSecret), providedSignature);
        }

        return false;
    }

    public static string Canonicalize(AdminMachineProof proof)
    {
        return string.Join(
            '\n',
            proof.Method.ToUpperInvariant(),
            proof.PathAndQuery,
            proof.BodyHash.ToLowerInvariant(),
            proof.AssertionHash.ToLowerInvariant(),
            proof.AppId,
            proof.KeyId,
            proof.TimestampUnixSeconds.ToString(CultureInfo.InvariantCulture),
            proof.IdempotencyKey);
    }

    private static byte[] ComputeHmac(string canonical, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }

    private static bool ConstantTimeEquals(byte[] left, byte[] right)
    {
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool TryDecodeSignature(string signature, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(signature);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
