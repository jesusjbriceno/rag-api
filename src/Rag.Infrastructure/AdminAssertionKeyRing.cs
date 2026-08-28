using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Rag.Infrastructure;

public sealed class AdminAssertionKeyRing : IDisposable
{
    private Dictionary<string, RsaSecurityKey> _keys = null!;

    public AdminAssertionKeyRing(AdminAssertionOptions options)
    {
        options.Validate();
        var keys = new Dictionary<string, RsaSecurityKey>(StringComparer.Ordinal);
        try
        {
            foreach (var item in options.ValidationKeys)
            {
                keys.Add(item.KeyId!, CreateValidationKey(item));
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            foreach (var key in keys.Values)
            {
                key.Rsa?.Dispose();
            }

            throw new InvalidOperationException("Admin assertion validation key material is invalid.", exception);
        }

        _keys = keys;
    }

    public bool TryGetKey(string? keyId, out RsaSecurityKey key)
    {
        if (keyId is not null && _keys.TryGetValue(keyId, out var found))
        {
            key = found;
            return true;
        }

        key = null!;
        return false;
    }

    public void Remove(string keyId)
    {
        if (_keys.Remove(keyId, out var key))
        {
            key.Rsa?.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Rsa?.Dispose();
        }
    }

    private static RsaSecurityKey CreateValidationKey(AdminAssertionValidationKeyOptions options)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(options.PublicKeyPem!);
        return new RsaSecurityKey(rsa) { KeyId = options.KeyId };
    }
}
