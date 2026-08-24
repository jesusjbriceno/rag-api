using Rag.Domain;

namespace Rag.Application;

public sealed class ResourceNotFoundException : Exception
{
}

public sealed class IncompatibleEmbeddingProfilesException : Exception
{
}

public sealed record CollectionRepresentation(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record OperationStatusRepresentation(
    Guid Id,
    OperationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureStage);

public interface IEmbeddingProfileDefaults
{
    EmbeddingProfile DefaultProfile { get; }
}

public interface ICollectionCommandRepository
{
    Task<bool> NameExistsAsync(Guid serviceClientId, string normalizedName, CancellationToken cancellationToken);

    void Add(Collection collection);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IOperationStatusRepository
{
    Task<OperationStatusRepresentation?> GetAsync(Guid serviceClientId, Guid collectionId, Guid operationId, CancellationToken cancellationToken);
}

public sealed class CreateCollectionHandler(
    ICollectionCommandRepository repository,
    IEmbeddingProfileDefaults embeddingProfiles)
{
    public async Task<CollectionRepresentation> HandleAsync(Guid serviceClientId, string name, CancellationToken cancellationToken = default)
    {
        if (serviceClientId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new ArgumentException("A collection name containing at most 200 characters is required.", nameof(name));
        }

        var normalizedName = name.Trim().ToLowerInvariant();
        if (await repository.NameExistsAsync(serviceClientId, normalizedName, cancellationToken))
        {
            throw new ArgumentException("A collection with this name already exists.", nameof(name));
        }

        var collection = new Collection(Guid.NewGuid(), serviceClientId, name, DateTimeOffset.UtcNow, embeddingProfiles.DefaultProfile);
        repository.Add(collection);
        await repository.SaveChangesAsync(cancellationToken);
        return new CollectionRepresentation(collection.Id, collection.Name, collection.CreatedAt);
    }
}

public sealed class GetOperationStatusHandler(IOperationStatusRepository repository)
{
    public async Task<OperationStatusRepresentation> HandleAsync(
        Guid serviceClientId,
        Guid collectionId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        await repository.GetAsync(serviceClientId, collectionId, operationId, cancellationToken)
        ?? throw new ResourceNotFoundException();
}
