using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class IngestionRepository(
    IngestionDbContext dbContext,
    IDbContextFactory<IngestionDbContext> dbContextFactory) : IIngestionRepository
{
    public Task<Collection?> GetCollectionAsync(Guid collectionId, CancellationToken cancellationToken) =>
        dbContext.Collections.SingleOrDefaultAsync(collection => collection.Id == collectionId, cancellationToken);

    public Task<Document?> FindByExternalReferenceAsync(
        Guid collectionId,
        string externalReference,
        CancellationToken cancellationToken) =>
        dbContext.Documents.Include(document => document.Versions)
            .SingleOrDefaultAsync(
                document => document.CollectionId == collectionId && document.ExternalReference == externalReference,
                cancellationToken);

    public async Task<Document?> FindByExternalReferenceForConflictResolutionAsync(
        Guid collectionId,
        string externalReference,
        CancellationToken cancellationToken)
    {
        await using var freshContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await freshContext.Documents.AsNoTracking()
            .Include(document => document.Versions)
            .SingleOrDefaultAsync(
                document => document.CollectionId == collectionId && document.ExternalReference == externalReference,
                cancellationToken);
    }

    public bool IsExternalReferenceUniqueConstraintViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
        && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
        && postgresException.ConstraintName == "IX_documents_CollectionId_ExternalReference";

    public void AddDocument(Document document) => dbContext.Documents.Add(document);

    public void AddOperation(Operation operation) => dbContext.Operations.Add(operation);

    public void DiscardChanges() => dbContext.ChangeTracker.Clear();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
