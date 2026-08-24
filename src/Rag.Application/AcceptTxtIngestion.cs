using Microsoft.Extensions.DependencyInjection;
using Rag.Domain;

namespace Rag.Application;

public sealed record AcceptTxtIngestionCommand(
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
    Task<Collection?> GetCollectionAsync(Guid collectionId, CancellationToken cancellationToken);

    Task<Document?> FindByExternalReferenceAsync(Guid collectionId, string externalReference, CancellationToken cancellationToken);

    Task<Document?> FindByExternalReferenceForConflictResolutionAsync(
        Guid collectionId,
        string externalReference,
        CancellationToken cancellationToken);

    bool IsExternalReferenceUniqueConstraintViolation(Exception exception);

    void AddDocument(Document document);

    void AddOperation(Operation operation);

    void DiscardChanges();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
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
        if (command.CollectionId == Guid.Empty)
        {
            throw new ArgumentException("A collection id is required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new ArgumentException("A file name is required.", nameof(command));
        }

        ArgumentNullException.ThrowIfNull(command.Content);

        if (await repository.GetCollectionAsync(command.CollectionId, cancellationToken) is null)
        {
            throw new InvalidOperationException("The collection does not exist.");
        }

        var externalReference = NormalizeExternalReference(command.ExternalReference);
        var contentHash = ContentHash.FromBytes(command.Content);
        Document? document = null;

        if (externalReference is not null)
        {
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
        var contentReference = ContentReference.ForVersion(versionId);
        var version = document.AddVersion(versionId, command.FileName, contentHash, contentReference, now);
        var operation = Operation.CreatePending(version.Id, now);

        var storageAttempted = false;
        try
        {
            storageAttempted = true;
            await contentStore.StoreAsync(contentReference, contentHash, command.Content, cancellationToken);

            if (isNewDocument)
            {
                repository.AddDocument(document);
            }

            repository.AddOperation(operation);
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            repository.DiscardChanges();
            if (storageAttempted)
            {
                await DeleteContentBestEffortAsync(contentReference);
            }

            if (externalReference is not null && repository.IsExternalReferenceUniqueConstraintViolation(exception))
            {
                var persistedDocument = await FindPersistedDocumentBestEffortAsync(command.CollectionId, externalReference);
                var persistedVersion = persistedDocument?.FindVersion(contentHash);
                if (persistedVersion is not null)
                {
                    return new AcceptTxtIngestionResult(persistedDocument!.Id, persistedVersion.Id, null, true);
                }
            }

            throw;
        }

        return new AcceptTxtIngestionResult(document.Id, version.Id, operation.Id, false);
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

    private async Task<Document?> FindPersistedDocumentBestEffortAsync(Guid collectionId, string externalReference)
    {
        try
        {
            return await repository.FindByExternalReferenceForConflictResolutionAsync(
                collectionId,
                externalReference,
                CancellationToken.None);
        }
        catch
        {
            // The original persistence failure must remain observable.
            return null;
        }
    }
}

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AcceptTxtIngestionHandler>();
        services.AddScoped<QueryEmbeddingService>();
        return services;
    }
}
