using System.Security.Cryptography;
using System.Text;

namespace Rag.Companion.Protocol;

public static class CompanionSigner
{
    /// <summary>Lowercase hexadecimal SHA-256 of the exact UTF-8 body.</summary>
    public static string ComputeBodySha256(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    /// <summary>Base64(HMAC-SHA256(secret, canonical string)).</summary>
    public static string ComputeSignature(string secret, string canonicalString)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(canonicalString);
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonicalString)));
    }
}
