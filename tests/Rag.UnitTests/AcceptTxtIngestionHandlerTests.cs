using Rag.Application;
using Rag.Domain;

namespace Rag.UnitTests;

public sealed class AcceptTxtIngestionHandlerTests
{
    [Fact]
    public async Task Same_reference_and_hash_returns_existing_version_without_an_operation()
    {
        var repository = new InMemoryIngestionRepository();
        var collection = repository.AddCollection();
        var contentStore = new InMemoryContentStore();
        var handler = new AcceptTxtIngestionHandler(repository, contentStore);
        var command = new AcceptTxtIngestionCommand(collection.Id, "guide.txt", "same"u8.ToArray(), "source://guide");

        var first = await handler.HandleAsync(command);
        var duplicate = await handler.HandleAsync(command);

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.DocumentId, duplicate.DocumentId);
        Assert.Equal(first.DocumentVersionId, duplicate.DocumentVersionId);
        Assert.Null(duplicate.OperationId);
        Assert.Single(repository.Operations);
        Assert.Single(contentStore.StoredReferences);
    }

    [Fact]
    public async Task Changed_content_for_same_reference_creates_a_new_version_and_pending_operation()
    {
        var repository = new InMemoryIngestionRepository();
        var collection = repository.AddCollection();
        var handler = new AcceptTxtIngestionHandler(repository, new InMemoryContentStore());

        var first = await handler.HandleAsync(new AcceptTxtIngestionCommand(collection.Id, "guide.txt", "one"u8.ToArray(), "source://guide"));
        var changed = await handler.HandleAsync(new AcceptTxtIngestionCommand(collection.Id, "guide.txt", "two"u8.ToArray(), "source://guide"));

        var document = Assert.Single(repository.Documents);
        Assert.Equal(first.DocumentId, changed.DocumentId);
        Assert.NotEqual(first.DocumentVersionId, changed.DocumentVersionId);
        Assert.Equal(2, document.Versions.Count);
        Assert.Equal(2, repository.Operations.Count);
        Assert.All(repository.Operations, operation => Assert.Equal(OperationStatus.Pending, operation.Status));
    }

    [Fact]
    public async Task Missing_external_reference_always_creates_a_new_document()
    {
        var repository = new InMemoryIngestionRepository();
        var collection = repository.AddCollection();
        var handler = new AcceptTxtIngestionHandler(repository, new InMemoryContentStore());
        var command = new AcceptTxtIngestionCommand(collection.Id, "upload.txt", "same"u8.ToArray());

        var first = await handler.HandleAsync(command);
        var second = await handler.HandleAsync(command);

        Assert.NotEqual(first.DocumentId, second.DocumentId);
        Assert.Equal(2, repository.Documents.Count);
        Assert.Equal(2, repository.Operations.Count);
    }

    [Fact]
    public async Task Storage_failure_discards_metadata_and_attempts_cleanup_without_request_cancellation()
    {
        var repository = new InMemoryIngestionRepository();
        var collection = repository.AddCollection();
        var contentStore = new InMemoryContentStore { FailStore = true };
        var handler = new AcceptTxtIngestionHandler(repository, contentStore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new AcceptTxtIngestionCommand(collection.Id, "guide.txt", "content"u8.ToArray()),
            cancellation.Token));

        Assert.True(repository.DiscardChangesCalled);
        Assert.Empty(repository.Documents);
        Assert.Empty(repository.Operations);
        Assert.Empty(contentStore.StoredReferences);
        Assert.Equal(CancellationToken.None, contentStore.DeleteCancellationToken);
    }

    [Fact]
    public async Task Persistence_failure_discards_metadata_and_cleans_stored_content()
    {
        var repository = new InMemoryIngestionRepository { SaveFailure = new InvalidOperationException("database failure") };
        var collection = repository.AddCollection();
        var contentStore = new InMemoryContentStore();
        var handler = new AcceptTxtIngestionHandler(repository, contentStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new AcceptTxtIngestionCommand(collection.Id, "guide.txt", "content"u8.ToArray())));

        Assert.True(repository.DiscardChangesCalled);
        Assert.Empty(contentStore.StoredReferences);
    }

    [Fact]
    public async Task Unique_reference_conflict_returns_persisted_same_content_version()
    {
        var repository = new InMemoryIngestionRepository { SaveFailure = new UniqueReferenceException() };
        var collection = repository.AddCollection();
        var content = "content"u8.ToArray();
        var hash = ContentHash.FromBytes(content);
        var winner = new Document(Guid.NewGuid(), collection.Id, "source://guide", DateTimeOffset.UtcNow);
        var winnerVersion = winner.AddVersion(
            Guid.NewGuid(),
            "guide.txt",
            hash,
            ContentReference.ForVersion(Guid.NewGuid()),
            DateTimeOffset.UtcNow);
        repository.ConflictResolutionDocument = winner;
        var contentStore = new InMemoryContentStore();
        var handler = new AcceptTxtIngestionHandler(repository, contentStore);

        var result = await handler.HandleAsync(new AcceptTxtIngestionCommand(
            collection.Id,
            "guide.txt",
            content,
            "source://guide"));

        Assert.True(result.IsDuplicate);
        Assert.Equal(winner.Id, result.DocumentId);
        Assert.Equal(winnerVersion.Id, result.DocumentVersionId);
        Assert.Null(result.OperationId);
        Assert.True(repository.DiscardChangesCalled);
        Assert.Empty(contentStore.StoredReferences);
    }

    private sealed class InMemoryIngestionRepository : IIngestionRepository
    {
        public List<Collection> Collections { get; } = [];

        public List<Document> Documents { get; } = [];

        public List<Operation> Operations { get; } = [];

        public Exception? SaveFailure { get; init; }

        public Document? ConflictResolutionDocument { get; set; }

        public bool DiscardChangesCalled { get; private set; }

        public Collection AddCollection()
        {
            var collection = new Collection(Guid.NewGuid(), "Test collection", DateTimeOffset.UtcNow);
            Collections.Add(collection);
            return collection;
        }

        public Task<Collection?> GetCollectionAsync(Guid collectionId, CancellationToken cancellationToken) =>
            Task.FromResult(Collections.SingleOrDefault(collection => collection.Id == collectionId));

        public Task<Document?> FindByExternalReferenceAsync(Guid collectionId, string externalReference, CancellationToken cancellationToken) =>
            Task.FromResult(Documents.SingleOrDefault(document =>
                document.CollectionId == collectionId && document.ExternalReference == externalReference));

        public Task<Document?> FindByExternalReferenceForConflictResolutionAsync(
            Guid collectionId,
            string externalReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConflictResolutionDocument);

        public bool IsExternalReferenceUniqueConstraintViolation(Exception exception) => exception is UniqueReferenceException;

        public void AddDocument(Document document) => Documents.Add(document);

        public void AddOperation(Operation operation) => Operations.Add(operation);

        public void DiscardChanges()
        {
            DiscardChangesCalled = true;
            Documents.Clear();
            Operations.Clear();
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
            SaveFailure is null ? Task.FromResult(0) : Task.FromException<int>(SaveFailure);
    }

    private sealed class InMemoryContentStore : IImmutableContentStore
    {
        public List<ContentReference> StoredReferences { get; } = [];

        public bool FailStore { get; init; }

        public CancellationToken? DeleteCancellationToken { get; private set; }

        public Task StoreAsync(ContentReference reference, ContentHash contentHash, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            StoredReferences.Add(reference);
            return FailStore
                ? Task.FromException(new InvalidOperationException("storage failure"))
                : Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(ContentReference reference, ContentHash contentHash, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(new NotSupportedException());

        public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken)
        {
            DeleteCancellationToken = cancellationToken;
            StoredReferences.Remove(reference);
            return Task.CompletedTask;
        }
    }

    private sealed class UniqueReferenceException : Exception;
}
