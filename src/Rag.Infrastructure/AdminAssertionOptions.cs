namespace Rag.Infrastructure;

public sealed class AdminAssertionOptions
{
    public const string SectionName = "AdminAssertion";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public List<AdminAssertionValidationKeyOptions> ValidationKeys { get; set; } = [];

    public int LifetimeSeconds { get; set; } = 60;

    public int ClockSkewSeconds { get; set; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("Admin assertion issuer and audience must be configured.");
        }

        if (ValidationKeys.Count == 0 || ValidationKeys.Any(key => string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyPem)))
        {
            throw new InvalidOperationException("At least one valid admin assertion validation key must be configured.");
        }

        if (ValidationKeys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != ValidationKeys.Count)
        {
            throw new InvalidOperationException("Admin assertion validation key ids must be unique.");
        }

        if (LifetimeSeconds <= 0 || ClockSkewSeconds < 0)
        {
            throw new InvalidOperationException("Admin assertion lifetime and clock skew must be valid.");
        }
    }
}

public sealed class AdminAssertionValidationKeyOptions
{
    public string? KeyId { get; set; }

    public string? PublicKeyPem { get; set; }
}
