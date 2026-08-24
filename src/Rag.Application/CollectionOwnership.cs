namespace Rag.Application;

public sealed record UnownedCollection(Guid Id, string Name, DateTimeOffset CreatedAt);

public interface ICollectionOwnershipRepository
{
    Task<IReadOnlyList<UnownedCollection>> ListUnownedAsync(CancellationToken cancellationToken);

    Task AssignOwnerAsync(Guid collectionId, Guid serviceClientId, CancellationToken cancellationToken);
}

public sealed class CollectionOwnershipOperator(ICollectionOwnershipRepository repository)
{
    public Task<IReadOnlyList<UnownedCollection>> ListUnownedAsync(CancellationToken cancellationToken = default) =>
        repository.ListUnownedAsync(cancellationToken);

    public Task AssignOwnerAsync(Guid collectionId, Guid serviceClientId, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty || serviceClientId == Guid.Empty)
        {
            throw new ArgumentException("Collection and service client ids must be non-empty GUIDs.");
        }

        return repository.AssignOwnerAsync(collectionId, serviceClientId, cancellationToken);
    }
}
