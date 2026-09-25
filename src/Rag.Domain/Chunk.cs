using System.Text;

namespace Rag.Domain;

public sealed class Chunk
{
    private Chunk()
    {
    }

    public Chunk(Guid id, Guid documentVersionId, int ordinal, string text)
    {
        if (id == Guid.Empty || documentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Chunk and document version ids are required.");
        }

        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Chunk text is required.", nameof(text));
        }

        if (text != text.Trim() || text.Contains('\r') || text != text.Normalize(NormalizationForm.FormC))
        {
            throw new ArgumentException("Chunk text must be normalized.", nameof(text));
        }

        if (text.EnumerateRunes().Count() > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "Chunk text cannot exceed 2,000 characters.");
        }

        Id = id;
        DocumentVersionId = documentVersionId;
        Ordinal = ordinal;
        Text = text;
    }

    public Guid Id { get; private set; }

    public Guid DocumentVersionId { get; private set; }

    public int Ordinal { get; private set; }

    public string Text { get; private set; } = null!;
}
