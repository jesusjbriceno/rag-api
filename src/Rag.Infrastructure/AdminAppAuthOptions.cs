namespace Rag.Infrastructure;

public sealed class AdminAppAuthOptions
{
    public const string SectionName = "AdminAppAuth";

    public List<AdminAppOptions> Apps { get; set; } = [];

    public int ClockSkewSeconds { get; set; } = 60;

    public void Validate()
    {
        if (Apps.Count == 0)
        {
            throw new InvalidOperationException("At least one admin app must be configured.");
        }

        foreach (var app in Apps)
        {
            if (string.IsNullOrWhiteSpace(app.AppId) ||
                string.IsNullOrWhiteSpace(app.KeyId) ||
                string.IsNullOrWhiteSpace(app.CurrentSecret))
            {
                throw new InvalidOperationException("Each admin app requires an app id, key id, and current secret.");
            }

            if (app.PreviousSecret is not null &&
                string.Equals(app.PreviousSecret, app.CurrentSecret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Admin app current and previous secrets must differ.");
            }
        }

        if (Apps.Select(app => app.AppId).Distinct(StringComparer.Ordinal).Count() != Apps.Count)
        {
            throw new InvalidOperationException("Admin app ids must be unique.");
        }

        if (Apps.Select(app => app.KeyId).Distinct(StringComparer.Ordinal).Count() != Apps.Count)
        {
            throw new InvalidOperationException("Admin app key ids must be unique.");
        }

        if (ClockSkewSeconds < 0)
        {
            throw new InvalidOperationException("Admin app clock skew must be non-negative.");
        }
    }
}

public sealed class AdminAppOptions
{
    public string? AppId { get; set; }

    public string? KeyId { get; set; }

    public string? CurrentSecret { get; set; }

    public string? PreviousSecret { get; set; }
}
