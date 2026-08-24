namespace Rag.Domain;

public sealed class ServiceClient
{
    private ServiceClient()
    {
    }

    public ServiceClient(Guid id, string name, DateTimeOffset createdAt)
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
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
}
