namespace Rag.Domain;

public sealed class Document
{
    private readonly List<DocumentVersion> _versions = [];

    private Document()
    {
    }

    public Document(Guid id, Guid collectionId, string? externalReference, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || collectionId == Guid.Empty)
        {
            throw new ArgumentException("Document and collection ids are required.");
        }

        Id = id;
        CollectionId = collectionId;
        ExternalReference = NormalizeExternalReference(externalReference);
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid CollectionId { get; private set; }

    public string? ExternalReference { get; private set; }

    public Guid CurrentVersionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<DocumentVersion> Versions => _versions;

    public DocumentVersion AddVersion(Guid versionId, string fileName, ContentHash contentHash, ContentReference contentReference, DateTimeOffset createdAt)
    {
        var version = new DocumentVersion(versionId, Id, _versions.Count + 1, fileName, contentHash, contentReference, createdAt);
        _versions.Add(version);
        CurrentVersionId = version.Id;
        return version;
    }

    public DocumentVersion? FindVersion(ContentHash contentHash) => _versions.SingleOrDefault(version => version.ContentHash == contentHash);

    private static string? NormalizeExternalReference(string? externalReference) =>
        string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim();
}
