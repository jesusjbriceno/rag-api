using System.Text;

namespace Rag.Companion.Adapters;

/// <summary>
/// Deterministically canonicalizes extracted text into a stable UTF-8 form suitable for hashing
/// and ingestion: strips a leading byte-order mark, maps CRLF and lone CR to LF, applies Unicode
/// NFC composition, preserves all whitespace verbatim, and guarantees a single terminal LF on
/// non-empty text. Empty input stays empty.
/// </summary>
public static class Normalizer
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = text;

        // Strip a single leading byte-order mark (U+FEFF). A UTF-8 BOM decodes to this one char.
        if (result.Length > 0 && result[0] == '\uFEFF')
        {
            result = result[1..];
        }

        // Map CRLF first, then any remaining lone CR, so all line endings become LF.
        result = result.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        // Canonical composition (NFC) so visually-equivalent composed/decomposed forms equalize.
        result = result.Normalize(NormalizationForm.FormC);

        // Guarantee a terminal LF for non-empty text; internal and trailing whitespace is otherwise preserved.
        if (result.Length > 0 && result[^1] != '\n')
        {
            result += "\n";
        }

        return result;
    }
}
