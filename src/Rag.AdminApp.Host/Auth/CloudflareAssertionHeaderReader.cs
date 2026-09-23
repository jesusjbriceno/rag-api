namespace Rag.AdminApp.Host;

/// <summary>
/// Reads the single non-empty Cloudflare Access assertion header. Missing, blank,
/// or multiple values fail closed and return null.
/// </summary>
public sealed class CloudflareAssertionHeaderReader
{
    public const string HeaderName = "Cf-Access-Jwt-Assertion";

    public string? Read(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1)
        {
            return null;
        }

        var value = values[0];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
