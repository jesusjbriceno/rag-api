using Microsoft.Extensions.DependencyInjection;
using Rag.Domain;

namespace Rag.Application;

public sealed record AcceptTxtIngestionCommand(
    Guid ServiceClientId,
    Guid CollectionId,
    string FileName,
    byte[] Content,
    string? ExternalReference = null);

public sealed record AcceptTxtIngestionResult(
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid? OperationId,
    bool IsDuplicate);

public interface IIngestionRepository
{
    Task<Collection?> GetCollectionAsync(Guid serviceClientId, Guid collectionId, CancellationToken cancellationToken);

    Task<IExternalReferenceTransaction> BeginExternalReferenceTransactionAsync(
        Guid collectionId,
        string externalReference,
        CancellationToken cancellationToken);

    Task<Document?> FindByExternalReferenceAsync(Guid collectionId, string externalReference, CancellationToken cancellationToken);

    bool IsExternalReferenceUniqueConstraintViolation(Exception exception);

    void AddDocument(Document document);

    void AddDocumentVersion(DocumentVersion version);

    void AddOperation(Operation operation);

    void DiscardChanges();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IExternalReferenceTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IImmutableContentStore
{
    Task StoreAsync(ContentReference reference, ContentHash contentHash, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(ContentReference reference, ContentHash contentHash, CancellationToken cancellationToken);

    Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken);
}

public sealed class AcceptTxtIngestionHandler(IIngestionRepository repository, IImmutableContentStore contentStore)
{
    public async Task<AcceptTxtIngestionResult> HandleAsync(AcceptTxtIngestionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ServiceClientId == Guid.Empty || command.CollectionId == Guid.Empty)
        {
            throw new ArgumentException("A collection id is required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new ArgumentException("A file name is required.", nameof(command));
        }

        if (!command.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .txt files are supported.", nameof(command));
        }

        ArgumentNullException.ThrowIfNull(command.Content);

        if (await repository.GetCollectionAsync(command.ServiceClientId, command.CollectionId, cancellationToken) is null)
        {
            throw new ResourceNotFoundException();
        }

        var externalReference = NormalizeExternalReference(command.ExternalReference);
        var contentHash = ContentHash.FromBytes(command.Content);
        Document? document = null;
        IExternalReferenceTransaction? externalReferenceTransaction = null;

        var storageAttempted = false;
        ContentReference? contentReference = null;
        try
        {
            if (externalReference is not null)
            {
                externalReferenceTransaction = await repository.BeginExternalReferenceTransactionAsync(
                    command.CollectionId,
                    externalReference,
                    cancellationToken);
                document = await repository.FindByExternalReferenceAsync(command.CollectionId, externalReference, cancellationToken);
                var existingVersion = document?.FindVersion(contentHash);
                if (existingVersion is not null)
                {
                    return new AcceptTxtIngestionResult(document!.Id, existingVersion.Id, null, true);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var isNewDocument = document is null;
            document ??= new Document(Guid.NewGuid(), command.CollectionId, externalReference, now);
            var versionId = Guid.NewGuid();
            contentReference = ContentReference.ForVersion(versionId);
            var version = document.AddVersion(versionId, command.FileName, contentHash, contentReference, now);
            var operation = Operation.CreatePending(version.Id, now);

            storageAttempted = true;
            await contentStore.StoreAsync(contentReference, contentHash, command.Content, cancellationToken);

            if (isNewDocument)
            {
                repository.AddDocument(document);
            }
            else
            {
                repository.AddDocumentVersion(version);
            }

            repository.AddOperation(operation);
            await repository.SaveChangesAsync(cancellationToken);
            if (externalReferenceTransaction is not null)
            {
                await externalReferenceTransaction.CommitAsync(cancellationToken);
            }

            return new AcceptTxtIngestionResult(document.Id, version.Id, operation.Id, false);
        }
        catch (Exception exception)
        {
            repository.DiscardChanges();
            if (storageAttempted && contentReference is not null)
            {
                await DeleteContentBestEffortAsync(contentReference);
            }

            if (externalReferenceTransaction is not null)
            {
                await externalReferenceTransaction.DisposeAsync();
                externalReferenceTransaction = null;
            }

            if (externalReference is not null && repository.IsExternalReferenceUniqueConstraintViolation(exception))
            {
                return await RecoverExternalReferenceConflictAsync(command, externalReference, contentHash);
            }

            throw;
        }
        finally
        {
            if (externalReferenceTransaction is not null)
            {
                await externalReferenceTransaction.DisposeAsync();
            }
        }
    }

    private async Task<AcceptTxtIngestionResult> RecoverExternalReferenceConflictAsync(
        AcceptTxtIngestionCommand command,
        string externalReference,
        ContentHash contentHash)
    {
        await using var transaction = await repository.BeginExternalReferenceTransactionAsync(
            command.CollectionId,
            externalReference,
            CancellationToken.None);
        var persistedDocument = await repository.FindByExternalReferenceAsync(
            command.CollectionId,
            externalReference,
            CancellationToken.None);
        var persistedVersion = persistedDocument?.FindVersion(contentHash);
        if (persistedVersion is not null)
        {
            return new AcceptTxtIngestionResult(persistedDocument!.Id, persistedVersion.Id, null, true);
        }

        if (persistedDocument is null)
        {
            throw new InvalidOperationException("The document was not persisted after its external-reference uniqueness conflict.");
        }

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var contentReference = ContentReference.ForVersion(versionId);
        var version = persistedDocument.AddVersion(versionId, command.FileName, contentHash, contentReference, now);
        var operation = Operation.CreatePending(version.Id, now);
        var storageAttempted = false;
        try
        {
            storageAttempted = true;
            await contentStore.StoreAsync(contentReference, contentHash, command.Content, CancellationToken.None);
            repository.AddDocumentVersion(version);
            repository.AddOperation(operation);
            await repository.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return new AcceptTxtIngestionResult(persistedDocument.Id, version.Id, operation.Id, false);
        }
        catch
        {
            repository.DiscardChanges();
            if (storageAttempted)
            {
                await DeleteContentBestEffortAsync(contentReference);
            }

            throw;
        }
    }

    private static string? NormalizeExternalReference(string? externalReference) =>
        string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim();

    private async Task DeleteContentBestEffortAsync(ContentReference contentReference)
    {
        try
        {
            await contentStore.DeleteAsync(contentReference, CancellationToken.None);
        }
        catch
        {
            // The original ingestion failure must remain observable.
        }
    }
}

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AcceptTxtIngestionHandler>();
        services.AddScoped<QueryEmbeddingService>();
        services.AddScoped<SemanticRetrievalHandler>();
        services.AddScoped<CredentialExchangeHandler>();
        services.AddScoped<CredentialOperator>();
        services.AddScoped<CollectionOwnershipOperator>();
        services.AddScoped<CreateCollectionHandler>();
        services.AddScoped<GetOperationStatusHandler>();
        return services;
    }
}
