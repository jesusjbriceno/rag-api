namespace Rag.Domain;

public sealed class DocumentVersion
{
    private DocumentVersion()
    {
    }

    public DocumentVersion(
        Guid id,
        Guid documentId,
        int number,
        string fileName,
        ContentHash contentHash,
        ContentReference contentReference,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || documentId == Guid.Empty)
        {
            throw new ArgumentException("Document and version ids are required.");
        }

        if (number < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A file name is required.", nameof(fileName));
        }

        Id = id;
        DocumentId = documentId;
        Number = number;
        FileName = fileName.Trim();
        MimeType = "text/plain";
        ContentHash = contentHash;
        ContentReference = contentReference;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid DocumentId { get; private set; }

    public int Number { get; private set; }

    public string FileName { get; private set; } = null!;

    public string MimeType { get; private set; } = null!;

    public ContentHash ContentHash { get; private set; }

    public ContentReference ContentReference { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
}
