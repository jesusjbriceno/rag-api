using System.Text;
using Rag.Domain;

namespace Rag.Application;

public sealed class TxtChunker
{
    public const int MaximumChunkCharacters = 2_000;
    public const int OverlapCharacters = 200;

    public IReadOnlyList<Chunk> Chunk(Guid documentVersionId, string content)
    {
        if (documentVersionId == Guid.Empty)
        {
            throw new ArgumentException("A document version id is required.", nameof(documentVersionId));
        }

        var normalized = Normalize(content);
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("TXT content does not contain processable text.");
        }

        var chunks = new List<Chunk>();
        var start = 0;
        while (start < normalized.Length)
        {
            var limit = AdvanceByRunes(normalized, start, MaximumChunkCharacters);
            var end = limit == normalized.Length
                ? normalized.Length
                : FindPreferredBoundary(normalized, start, limit);

            if (end <= start)
            {
                end = limit;
            }

            chunks.Add(new Chunk(Guid.NewGuid(), documentVersionId, chunks.Count + 1, normalized[start..end]));
            if (end == normalized.Length)
            {
                break;
            }

            start = RetreatByRunes(normalized, end, OverlapCharacters);
        }

        return chunks;
    }

    public static string Normalize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var paragraphs = new List<string>();
        var paragraphLines = new List<string>();
        var lineNormalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in lineNormalized.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                AddParagraph(paragraphs, paragraphLines);
                continue;
            }

            paragraphLines.Add(CollapseWhitespace(line));
        }

        AddParagraph(paragraphs, paragraphLines);
        return string.Join("\n\n", paragraphs).Normalize(NormalizationForm.FormC);
    }

    private static int FindPreferredBoundary(string content, int start, int limit)
    {
        var minimumBoundary = AdvanceByRunes(content, start, OverlapCharacters);
        var paragraphBoundary = content.LastIndexOf("\n\n", limit - 1, StringComparison.Ordinal);
        if (paragraphBoundary > minimumBoundary)
        {
            return paragraphBoundary;
        }

        for (var index = limit - 1; index > minimumBoundary; index--)
        {
            if (char.IsWhiteSpace(content[index]))
            {
                return index;
            }
        }

        return limit;
    }

    private static int AdvanceByRunes(string value, int start, int count)
    {
        var index = start;
        while (count > 0 && index < value.Length)
        {
            index += Rune.GetRuneAt(value, index).Utf16SequenceLength;
            count--;
        }

        return index;
    }

    private static int RetreatByRunes(string value, int end, int count)
    {
        var index = end;
        while (count > 0 && index > 0)
        {
            index--;
            if (index > 0 && char.IsLowSurrogate(value[index]) && char.IsHighSurrogate(value[index - 1]))
            {
                index--;
            }

            count--;
        }

        return index;
    }

    private static void AddParagraph(List<string> paragraphs, List<string> paragraphLines)
    {
        if (paragraphLines.Count == 0)
        {
            return;
        }

        paragraphs.Add(CollapseWhitespace(string.Join(" ", paragraphLines)));
        paragraphLines.Clear();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString();
    }
}
