namespace Rag.Infrastructure;

public sealed class AdminAssertionReplay
{
    private AdminAssertionReplay()
    {
    }

    public AdminAssertionReplay(
        Guid id,
        string issuer,
        string jti,
        string appId,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A replay id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new ArgumentException("An issuer is required.", nameof(issuer));
        }

        if (string.IsNullOrWhiteSpace(jti))
        {
            throw new ArgumentException("A JWT id is required.", nameof(jti));
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("An app id is required.", nameof(appId));
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("A replay reservation must expire after it is created.", nameof(expiresAt));
        }

        Id = id;
        Issuer = issuer.Trim();
        Jti = jti.Trim();
        AppId = appId.Trim();
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Issuer { get; private set; } = null!;

    public string Jti { get; private set; } = null!;

    public string AppId { get; private set; } = null!;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
