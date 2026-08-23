namespace Rag.Domain;

public sealed record ContentReference
{
    public ContentReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A content reference is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static ContentReference ForVersion(Guid versionId) => new($"versions/{versionId:N}.txt");

    public override string ToString() => Value;
}
