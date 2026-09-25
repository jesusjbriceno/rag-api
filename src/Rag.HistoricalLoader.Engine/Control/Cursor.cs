using System.Globalization;
using System.Text;
using Rag.HistoricalLoader.Core.Lifecycle;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The opaque, URL-safe, versioned encoding of a document page's keyset position. The payload is exactly the
/// version tag, the durable UTC ticks and the run-document id of the last row of the page — no path, source
/// key, offsets, or free-form text can travel in a cursor, and a cursor that does not decode, is not
/// version 1, or carries a non-GUID identity is refused so the client must resynchronise.
/// </summary>
public readonly record struct ControlDocumentCursor(Guid RunDocumentId, DateTimeOffset CreatedAt)
{
    /// <summary>The only supported cursor version. A client can never negotiate a different one silently.</summary>
    public const string Version = "v1";

    /// <summary>Encodes the typed store position into its opaque wire form.</summary>
    public static string Encode(DocumentPageCursor position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return Encode(new ControlDocumentCursor(position.RunDocumentId, position.CreatedAt));
    }

    /// <summary>Encodes this cursor into its opaque wire form.</summary>
    public static string Encode(ControlDocumentCursor cursor)
        => EncodePosition(cursor.CreatedAt.UtcTicks, cursor.RunDocumentId);

    /// <summary>
    /// Decodes an opaque cursor. Returns <see langword="false"/> for a missing, non-base64url, non-UTF-8,
    /// wrong-version, or malformed payload; the caller maps that to a resynchronisation request.
    /// </summary>
    public static bool TryDecode(string? cursor, out ControlDocumentCursor decoded)
    {
        decoded = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var text = TryDecodeBase64Url(cursor);
        if (text is null)
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var utcTicks)
            || utcTicks < DateTimeOffset.MinValue.UtcTicks
            || utcTicks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParse(parts[2], out var runDocumentId))
        {
            return false;
        }

        decoded = new ControlDocumentCursor(runDocumentId, new DateTimeOffset(utcTicks, TimeSpan.Zero));
        return true;
    }

    /// <summary>Projects this cursor onto the typed store position the page read consumes.</summary>
    public DocumentPageCursor ToPageCursor() => new(CreatedAt, RunDocumentId);

    private static string EncodePosition(long utcTicks, Guid runDocumentId)
        => EncodeBytes(Encoding.UTF8.GetBytes(
            $"{Version}.{utcTicks.ToString(CultureInfo.InvariantCulture)}.{runDocumentId}"));

    private static string EncodeBytes(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? TryDecodeBase64Url(string cursor)
    {
        var normalized = cursor.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
