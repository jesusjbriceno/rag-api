using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class IngestionRepository(IngestionDbContext dbContext) : IIngestionRepository
{
    public Task<Collection?> GetCollectionAsync(Guid serviceClientId, Guid collectionId, CancellationToken cancellationToken) =>
        dbContext.Collections.SingleOrDefaultAsync(
            collection => collection.Id == collectionId && collection.ServiceClientId == serviceClientId,
            cancellationToken);

    public Task<Document?> FindByExternalReferenceAsync(
        Guid collectionId,
        string externalReference,
        CancellationToken cancellationToken) =>
        dbContext.Documents.Include(document => document.Versions)
            .SingleOrDefaultAsync(
                document => document.CollectionId == collectionId && document.ExternalReference == externalReference,
                cancellationToken);

    public async Task<IExternalReferenceTransaction> BeginExternalReferenceTransactionAsync(
        Guid collectionId,
        string externalReference,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var lockKey = $"{collectionId:N}:{externalReference}";
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                cancellationToken);
            return new ExternalReferenceTransaction(transaction);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            throw;
        }
    }

    public bool IsExternalReferenceUniqueConstraintViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
        && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
        && postgresException.ConstraintName == "IX_documents_CollectionId_ExternalReference";

    public void AddDocument(Document document) => dbContext.Documents.Add(document);

    public void AddDocumentVersion(DocumentVersion version) => dbContext.DocumentVersions.Add(version);

    public void AddOperation(Operation operation) => dbContext.Operations.Add(operation);

    public void DiscardChanges() => dbContext.ChangeTracker.Clear();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);

    private sealed class ExternalReferenceTransaction(IDbContextTransaction transaction) : IExternalReferenceTransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            await transaction.DisposeAsync();
        }
    }
}
