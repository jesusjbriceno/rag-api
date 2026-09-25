using System.Security.Cryptography;

namespace Rag.Domain;

public readonly record struct ContentHash
{
    public ContentHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 hash must be 64 hexadecimal characters.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public static ContentHash FromBytes(ReadOnlySpan<byte> content) => new(Convert.ToHexString(SHA256.HashData(content)));

    public override string ToString() => Value;
}
