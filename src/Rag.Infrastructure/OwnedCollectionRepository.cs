using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class ConfiguredEmbeddingProfileDefaults(IOptions<EmbeddingOptions> options) : IEmbeddingProfileDefaults
{
    public EmbeddingProfile DefaultProfile => options.Value.Validate().DefaultProfile;
}

public sealed class OwnedCollectionRepository(IngestionDbContext dbContext) : ICollectionCommandRepository, IOperationStatusRepository
{
    public Task<bool> NameExistsAsync(Guid serviceClientId, string normalizedName, CancellationToken cancellationToken) =>
        dbContext.Collections.AnyAsync(
            collection => collection.ServiceClientId == serviceClientId && collection.Name.ToLower() == normalizedName,
            cancellationToken);

    public void Add(Collection collection) => dbContext.Collections.Add(collection);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new ArgumentException("A collection with this name already exists.", nameof(Collection.Name), exception);
        }
    }

    public async Task<OperationStatusRepresentation?> GetAsync(
        Guid serviceClientId,
        Guid collectionId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await (
            from item in dbContext.Operations.AsNoTracking()
            join version in dbContext.DocumentVersions.AsNoTracking() on item.DocumentVersionId equals version.Id
            join document in dbContext.Documents.AsNoTracking() on version.DocumentId equals document.Id
            join collection in dbContext.Collections.AsNoTracking() on document.CollectionId equals collection.Id
            where item.Id == operationId && collection.Id == collectionId && collection.ServiceClientId == serviceClientId
            select new
            {
                item.Id,
                item.Status,
                item.CreatedAt,
                item.StartedAt,
                item.CompletedAt,
                item.FailureStage,
            }).SingleOrDefaultAsync(cancellationToken);

        return operation is null
            ? null
            : new OperationStatusRepresentation(
                operation.Id,
                operation.Status,
                operation.CreatedAt,
                operation.StartedAt,
                operation.CompletedAt,
                SafeFailureStage(operation.FailureStage));
    }

    // Keyset page over one client's collections. The ordering and cursor semantics are the administration
    // plane's (see AdminRepository.ListClientsAsync): (CreatedAt, Id) ascending, with the decoded cursor
    // resuming strictly after its key, so consecutive pages produced by AdminCursor.Encode have no
    // duplicates and no gaps.
    public async Task<AdminPage<Collection>> ListCollectionsAsync(
        Guid serviceClientId,
        int limit,
        AdminCursorKey? cursor,
        CancellationToken cancellationToken)
    {
        IQueryable<Collection> query = dbContext.Collections
            .AsNoTracking()
            .Where(collection => collection.ServiceClientId == serviceClientId);
        if (cursor is { } key)
        {
            query = query.Where(collection =>
                collection.CreatedAt > key.Timestamp ||
                (collection.CreatedAt == key.Timestamp && collection.Id.CompareTo(key.Id) > 0));
        }

        var items = await query
            .OrderBy(collection => collection.CreatedAt)
            .ThenBy(collection => collection.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        return TrimPage(items, limit);
    }

    private static AdminPage<Collection> TrimPage(List<Collection> items, int limit)
    {
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new AdminPage<Collection>(items, hasMore);
    }

    private static string? SafeFailureStage(string? stage) => stage is "load" or "parse" or "embed" or "index" ? stage : null;
}
