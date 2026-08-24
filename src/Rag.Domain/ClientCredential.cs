namespace Rag.Domain;

public enum CredentialStatus
{
    Active,
    Revoked,
}

public sealed class ClientCredential
{
    private ClientCredential()
    {
    }

    public ClientCredential(
        Guid id,
        Guid serviceClientId,
        string keyId,
        byte[] secretHash,
        byte[] secretSalt,
        int hashVersion,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null)
    {
        if (id == Guid.Empty || serviceClientId == Guid.Empty)
        {
            throw new ArgumentException("Credential and service client ids are required.");
        }

        if (!IsValidKeyId(keyId))
        {
            throw new ArgumentException("The credential key id is invalid.", nameof(keyId));
        }

        if (secretHash.Length != 32 || secretSalt.Length != 16 || hashVersion < 1)
        {
            throw new ArgumentException("The credential secret material is invalid.");
        }

        if (expiresAt is not null && expiresAt <= createdAt)
        {
            throw new ArgumentException("Credential expiry must be after creation.", nameof(expiresAt));
        }

        Id = id;
        ServiceClientId = serviceClientId;
        KeyId = keyId;
        SecretHash = secretHash;
        SecretSalt = secretSalt;
        HashVersion = hashVersion;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Version = 1;
        Status = CredentialStatus.Active;
    }

    public Guid Id { get; private set; }

    public Guid ServiceClientId { get; private set; }

    public string KeyId { get; private set; } = null!;

    public byte[] SecretHash { get; private set; } = null!;

    public byte[] SecretSalt { get; private set; } = null!;

    public int HashVersion { get; private set; }

    public int Version { get; private set; }

    public CredentialStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActiveAt(DateTimeOffset now) =>
        Status == CredentialStatus.Active && (ExpiresAt is null || ExpiresAt > now);

    public void Rotate(byte[] secretHash, byte[] secretSalt, int hashVersion, DateTimeOffset now)
    {
        if (!IsActiveAt(now))
        {
            throw new InvalidOperationException("Only an active credential can be rotated.");
        }

        if (secretHash.Length != 32 || secretSalt.Length != 16 || hashVersion < 1)
        {
            throw new ArgumentException("The credential secret material is invalid.");
        }

        SecretHash = secretHash;
        SecretSalt = secretSalt;
        HashVersion = hashVersion;
        Version = checked(Version + 1);
    }

    public void Revoke(DateTimeOffset now)
    {
        if (Status == CredentialStatus.Revoked)
        {
            return;
        }

        Status = CredentialStatus.Revoked;
        RevokedAt = now;
        Version = checked(Version + 1);
    }

    public static bool IsValidKeyId(string? value) =>
        value is { Length: 27 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
