using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Rag.Companion.Protocol;

namespace Rag.Companion.Tests;

public sealed record SignedRequest(string Body, string BodySha256, string Nonce, string Signature);

/// <summary>
/// In-process fake of the (not yet implemented) BFF companion endpoints. It independently
/// verifies the canonical HMAC signature, body hash, timestamp freshness, and nonce
/// single-use before responding, so "accepted" proves the client actually signed correctly.
/// </summary>
public sealed class CompanionBffHandler : HttpMessageHandler
{
    private readonly string _secret;
    private readonly string _companionId;
    private readonly string _keyId;
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _seenNonces = new(StringComparer.Ordinal);

    public CompanionBffHandler(string secret, string companionId, string keyId, TimeProvider clock)
    {
        _secret = secret;
        _companionId = companionId;
        _keyId = keyId;
        _clock = clock;
    }

    public LeaseResponse? NextLease { get; set; }
    public EventResponse? NextEvent { get; set; }
    public HttpStatusCode? ForcedStatus { get; set; }
    public IReadOnlyCollection<string> SeenNonces => _seenNonces;
    public List<SignedRequest> SignedRequests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ForcedStatus is { } forced)
        {
            return Task.FromResult(new HttpResponseMessage(forced));
        }

        var rejected = Verify(request);
        if (rejected != HttpStatusCode.OK)
        {
            return Task.FromResult(new HttpResponseMessage(rejected));
        }

        if (request.RequestUri!.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal))
        {
            return NextLease is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
                : Task.FromResult(JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(NextLease, ProtocolJson.Options)));
        }

        var @event = NextEvent ?? new EventResponse { AcceptedSequence = 1, State = CompanionState.Running, Replayed = false };
        return Task.FromResult(JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(@event, ProtocolJson.Options)));
    }

    private HttpStatusCode Verify(HttpRequestMessage request)
    {
        var id = Single(request, "X-Companion-Id");
        var keyId = Single(request, "X-Companion-Key-Id");
        var timestampHeader = Single(request, "X-Companion-Timestamp");
        var nonce = Single(request, "X-Companion-Nonce");
        var bodySha = Single(request, "X-Companion-Body-SHA256");
        var signature = Single(request, "X-Companion-Signature");

        if (id is null || keyId is null || timestampHeader is null || nonce is null || bodySha is null || signature is null)
        {
            return HttpStatusCode.Unauthorized;
        }

        if (id != _companionId || keyId != _keyId)
        {
            return HttpStatusCode.Unauthorized;
        }

        if (!long.TryParse(timestampHeader, out var timestamp) || Math.Abs(_clock.GetUtcNow().ToUnixTimeSeconds() - timestamp) > 60)
        {
            return HttpStatusCode.Unauthorized;
        }

        if (!_seenNonces.Add(nonce))
        {
            return HttpStatusCode.Unauthorized;
        }

        var bodyBytes = request.Content is null ? [] : request.Content.ReadAsByteArrayAsync(CancellationToken.None).GetAwaiter().GetResult();
        var body = Encoding.UTF8.GetString(bodyBytes);
        if (!string.Equals(CompanionSigner.ComputeBodySha256(body), bodySha, StringComparison.Ordinal))
        {
            return HttpStatusCode.Unauthorized;
        }

        var canonical = CanonicalString.Create(request.Method.Method, request.RequestUri!.AbsolutePath, bodySha, id, keyId, timestamp, nonce);
        if (!string.Equals(CompanionSigner.ComputeSignature(_secret, canonical.ToString()), signature, StringComparison.Ordinal))
        {
            return HttpStatusCode.Unauthorized;
        }

        SignedRequests.Add(new SignedRequest(body, bodySha, nonce, signature));
        return HttpStatusCode.OK;
    }

    private static string? Single(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
