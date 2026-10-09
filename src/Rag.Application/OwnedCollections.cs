using Rag.Domain;

namespace Rag.Application;

public sealed class ResourceNotFoundException : Exception
{
}

public sealed class IncompatibleEmbeddingProfilesException : Exception
{
}

public sealed record CollectionRepresentation(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record CollectionPage(IReadOnlyList<CollectionRepresentation> Items, string? NextCursor);

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

    Task<AdminPage<Collection>> ListCollectionsAsync(Guid serviceClientId, int limit, AdminCursorKey? cursor, CancellationToken cancellationToken);

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

// Mirrors the administration plane's listing handlers: the same limit and cursor validation, the same
// { items, nextCursor } page shape, and the same AdminCursor codec, so the wire format is identical
// across planes. The page ordering is (CreatedAt, Id) ascending — the cursor of a page is the last
// item's (CreatedAt, Id) and the next page resumes strictly after that key.
public sealed class ListCollectionsHandler(ICollectionCommandRepository repository)
{
    public async Task<CollectionPage> HandleAsync(
        Guid serviceClientId,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var pageSize = AdminSupport.ResolveLimit(limit);
        var key = AdminSupport.ResolveCursor(cursor);
        var page = await repository.ListCollectionsAsync(serviceClientId, pageSize, key, cancellationToken);
        var nextCursor = page.HasMore
            ? AdminCursor.Encode(new AdminCursorKey(page.Items[^1].CreatedAt, page.Items[^1].Id))
            : null;
        return new CollectionPage(
            page.Items.Select(item => new CollectionRepresentation(item.Id, item.Name, item.CreatedAt)).ToList(),
            nextCursor);
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
