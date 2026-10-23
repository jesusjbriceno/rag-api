namespace Rag.AdminApp.Host;

/// <summary>
/// BFF posture configuration. Every value is required: a missing or empty value fails
/// closed at startup instead of degrading enforcement silently.
/// </summary>
public sealed class AdminBffOptions
{
    public const string SectionName = "AdminBff";

    /// <summary>Absolute base URL of the upstream admin API.</summary>
    public string? UpstreamBaseUrl { get; set; }

    /// <summary>The exact browser origin allowed to call mutation routes (same-origin protection).</summary>
    public string? AllowedOrigin { get; set; }

    /// <summary>
    /// The explicit Cloudflare Access subject allowlist. Empty means no administrator
    /// may operate the BFF; an empty list must never be treated as "allow everyone".
    /// </summary>
    public List<string> AllowedSubjects { get; set; } = [];

    /// <summary>
    /// Optional directory of the built admin SPA (containing index.html). When unset,
    /// the host falls back to the repository's conventional apps/admin-ui/dist path.
    /// </summary>
    public string? SpaRoot { get; set; }

    public void Validate()
    {
        if (!Uri.TryCreate(UpstreamBaseUrl, UriKind.Absolute, out var upstream) ||
            upstream.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"AdminBff:UpstreamBaseUrl must be an absolute http(s) URL (got: '{UpstreamBaseUrl ?? "<null>"}').");
        }

        if (string.IsNullOrWhiteSpace(AllowedOrigin))
        {
            throw new InvalidOperationException("AdminBff:AllowedOrigin must be configured.");
        }

        if (AllowedSubjects.Count == 0 || AllowedSubjects.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "AdminBff:AllowedSubjects must name at least one administrator subject; the allowlist fails closed.");
        }
    }
}
