using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rag.AdminApp;

public sealed record AdminRequestSignerOptions(string AppId, string KeyId, string MachineSecret)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppId) || string.IsNullOrWhiteSpace(KeyId) || string.IsNullOrWhiteSpace(MachineSecret))
        {
            throw new ArgumentException("Admin request signing requires an app id, key id, and machine secret.");
        }
    }
}

/// <summary>
/// A browser-derived but BFF-fixed upstream operation: the method, the exact upstream
/// path, and the body bytes are chosen by the BFF, never by the browser.
/// </summary>
public sealed record AdminSignableRequest(string Method, string PathAndQuery, byte[] Body)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Method) || string.IsNullOrWhiteSpace(PathAndQuery) || Body is null)
        {
            throw new ArgumentException("A signable request requires a method, path and query, and a body.");
        }
    }
}

/// <summary>
/// The complete signed material for one upstream admin API request. The assertion JWS,
/// signature, and machine secret exist only server-side; the browser never receives
/// signing material, but the <see cref="Headers"/> set is what the upstream admin API
/// contract requires on the proxied request.
/// </summary>
public sealed record AdminSignedRequest(
    string Method,
    string PathAndQuery,
    string BodyHash,
    string AssertionJws,
    string AssertionHash,
    string AppId,
    string KeyId,
    long TimestampUnixSeconds,
    string Signature,
    string IdempotencyKey)
{
    public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["X-Admin-App-Id"] = AppId,
        ["X-Admin-Key-Id"] = KeyId,
        ["X-Admin-Timestamp"] = TimestampUnixSeconds.ToString(CultureInfo.InvariantCulture),
        ["X-Admin-Signature"] = Signature,
        ["X-Admin-Assertion"] = AssertionJws,
        ["Idempotency-Key"] = IdempotencyKey,
    };
}

/// <summary>
/// Signs a fixed upstream admin API request on behalf of a verified Cloudflare Access
/// subject: it issues a fresh short-lived assertion carrying that subject and applies
/// the API's machine-proof HMAC contract over the caller-supplied idempotency key.
///
/// The canonical string mirrors <c>AdminMachineProofVerifier.Canonicalize</c> in
/// Rag.Infrastructure — the canonicalization the API verifies. Rag.AdminApp
/// intentionally carries no dependency on Rag.Infrastructure, so the two must be kept
/// in sync; the unit tests cross-verify against the API's verifier.
/// </summary>
public sealed class AdminRequestSigner
{
    private readonly AdminAssertionIssuer _assertionIssuer;
    private readonly AdminRequestSignerOptions _options;

    public AdminRequestSigner(AdminAssertionIssuer assertionIssuer, AdminRequestSignerOptions options)
    {
        _assertionIssuer = assertionIssuer ?? throw new ArgumentNullException(nameof(assertionIssuer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public AdminSignedRequest Sign(string verifiedSubject, AdminSignableRequest request, DateTimeOffset now, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(verifiedSubject))
        {
            throw new ArgumentException("A verified subject is required.", nameof(verifiedSubject));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        request.Validate();

        // Fresh, short-lived assertion bound to the verified subject; the caller-supplied
        // idempotency key is used verbatim so an HTTP retry of one logical browser
        // operation deduplicates upstream instead of creating duplicates.
        var assertionJws = _assertionIssuer.Issue(verifiedSubject, now);
        var bodyHash = ComputeSha256Hex(request.Body);
        var assertionHash = ComputeSha256Hex(Encoding.UTF8.GetBytes(assertionJws));
        var timestampUnixSeconds = now.ToUnixTimeSeconds();

        var canonical = string.Join(
            '\n',
            request.Method.ToUpperInvariant(),
            request.PathAndQuery,
            bodyHash,
            assertionHash,
            _options.AppId,
            _options.KeyId,
            timestampUnixSeconds.ToString(CultureInfo.InvariantCulture),
            idempotencyKey);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MachineSecret));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));

        return new AdminSignedRequest(
            request.Method,
            request.PathAndQuery,
            bodyHash,
            assertionJws,
            assertionHash,
            _options.AppId,
            _options.KeyId,
            timestampUnixSeconds,
            signature,
            idempotencyKey);
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
