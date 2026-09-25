namespace Rag.Infrastructure;

public sealed class AdminAuditOptions
{
    public const string SectionName = "AdminAudit";

    public const string DaysMode = "days";

    public const string IndefiniteMode = "indefinite";

    public string? RetentionMode { get; set; }

    public int RetentionDays { get; set; }

    public string? NormalizedMode =>
        string.IsNullOrWhiteSpace(RetentionMode) ? null : RetentionMode.Trim();

    public bool IsIndefinite =>
        string.Equals(NormalizedMode, IndefiniteMode, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        var mode = NormalizedMode;
        if (string.IsNullOrWhiteSpace(mode) ||
            !(string.Equals(mode, DaysMode, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(mode, IndefiniteMode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Admin audit retention mode must be 'days' or 'indefinite'.");
        }

        if (string.Equals(mode, DaysMode, StringComparison.OrdinalIgnoreCase) && RetentionDays <= 0)
        {
            throw new InvalidOperationException(
                "Admin audit retention days must be positive when retention mode is 'days'.");
        }
    }
}
