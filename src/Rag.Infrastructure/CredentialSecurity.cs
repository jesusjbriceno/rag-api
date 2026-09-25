using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class CredentialGenerator : ICredentialGenerator
{
    public string GenerateKeyId() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(20));

    public string GenerateSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
}

public sealed class Argon2idCredentialSecretHasher : ICredentialSecretHasher
{
    public const int HashVersion = 1;
    private const string FallbackDummySecret = "invalid-credential-secret";
    private static readonly byte[] DummyHash = new byte[32];
    private static readonly byte[] DummySalt = new byte[16];

    public CredentialSecretHash Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var salt = RandomNumberGenerator.GetBytes(16);
        return new CredentialSecretHash(HashCore(secret, salt), salt, HashVersion);
    }

    public bool Verify(string secret, ClientCredential credential)
    {
        if (credential.HashVersion != HashVersion || string.IsNullOrEmpty(secret))
        {
            VerifyDummy(secret);
            return false;
        }

        var computed = HashCore(secret, credential.SecretSalt);
        try
        {
            return CryptographicOperations.FixedTimeEquals(computed, credential.SecretHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(computed);
        }
    }

    public void VerifyDummy(string? secret)
    {
        var computed = HashCore(secret is { Length: > 0 and <= 512 } ? secret : FallbackDummySecret, DummySalt);
        try
        {
            _ = CryptographicOperations.FixedTimeEquals(computed, DummyHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(computed);
        }
    }

    private static byte[] HashCore(string secret, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            Iterations = 3,
            MemorySize = 65_536,
        };
        return argon2.GetBytes(32);
    }
}
