namespace Rag.Domain;

public sealed class ServiceClient
{
    private ServiceClient()
    {
    }

    public ServiceClient(Guid id, string name, DateTimeOffset createdAt, string? description = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A service client id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new ArgumentException("A service client name is required and must not exceed 200 characters.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        Description = NormalizeDescription(description);
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var trimmed = description.Trim();
        if (trimmed.Length > 500)
        {
            throw new ArgumentException("A service client description must not exceed 500 characters.", nameof(description));
        }

        return trimmed;
    }
}
