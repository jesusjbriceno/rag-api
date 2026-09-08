using System.Globalization;

namespace Rag.Companion.Protocol;

/// <summary>
/// The canonical signing envelope: seven newline-joined lines in a fixed order
/// (METHOD, PATH, BODY_SHA256, COMPANION_ID, KEY_ID, TIMESTAMP, NONCE).
/// </summary>
public readonly struct CanonicalString
{
    public CanonicalString(string method, string path, string bodySha256, string companionId, string keyId, string timestamp, string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodySha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        Method = method;
        Path = path;
        BodySha256 = bodySha256;
        CompanionId = companionId;
        KeyId = keyId;
        Timestamp = timestamp;
        Nonce = nonce;
    }

    public string Method { get; }
    public string Path { get; }
    public string BodySha256 { get; }
    public string CompanionId { get; }
    public string KeyId { get; }
    public string Timestamp { get; }
    public string Nonce { get; }

    public static CanonicalString Create(string method, string path, string bodySha256, string companionId, string keyId, long timestampUnixSeconds, string nonce) =>
        new(method, path, bodySha256, companionId, keyId, timestampUnixSeconds.ToString(CultureInfo.InvariantCulture), nonce);

    public override string ToString() => $"{Method}\n{Path}\n{BodySha256}\n{CompanionId}\n{KeyId}\n{Timestamp}\n{Nonce}";
}
