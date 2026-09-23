namespace Rag.HistoricalLoader.Engine.Security;

/// <summary>
/// Builds the Cloudflare Access service-token header pair at the outer HTTPS boundary. The type is
/// stateless: the secret exists only in the request header value it applies and is never retained or
/// written to a log/audit surface.
/// </summary>
public sealed class CloudflareHeaderBuilder
{
    public const string ClientIdHeader = "CF-Access-Client-Id";

    public const string ClientSecretHeader = "CF-Access-Client-Secret";

    public void Apply(HttpRequestMessage request, CloudflareServiceToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(token);

        request.Headers.Remove(ClientIdHeader);
        request.Headers.Remove(ClientSecretHeader);
        request.Headers.TryAddWithoutValidation(ClientIdHeader, token.ClientId);
        request.Headers.TryAddWithoutValidation(ClientSecretHeader, token.ClientSecret);
    }
}
