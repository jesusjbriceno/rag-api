using Rag.Application;
using Rag.Domain;

namespace Rag.UnitTests;

public sealed class SemanticRetrievalHandlerTests
{
    [Fact]
    public async Task Rejects_invalid_query_inputs_before_loading_profiles()
    {
        var profiles = new RecordingProfiles();
        var provider = new RecordingEmbeddingProvider();
        var repository = new RecordingRepository();
        var handler = new SemanticRetrievalHandler(profiles, provider, repository);

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(new SemanticRetrievalQuery([], "query", 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(new SemanticRetrievalQuery([Guid.NewGuid(), Guid.NewGuid()], " ", 1)));
        var collectionId = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(new SemanticRetrievalQuery([collectionId, collectionId], "query", 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => handler.HandleAsync(new SemanticRetrievalQuery([Guid.NewGuid()], "query", 0)));

        Assert.Empty(profiles.RequestedCollectionIds);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task Rejects_missing_profiles_before_embedding_or_searching()
    {
        var firstCollectionId = Guid.NewGuid();
        var missingCollectionId = Guid.NewGuid();
        var profiles = new RecordingProfiles
        {
            Profiles =
            {
                [firstCollectionId] = new EmbeddingProfile("ollama", "test:1", "1", 3),
            },
        };
        var provider = new RecordingEmbeddingProvider();
        var repository = new RecordingRepository();
        var handler = new SemanticRetrievalHandler(profiles, provider, repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new SemanticRetrievalQuery([firstCollectionId, missingCollectionId], "query", 1)));

        Assert.Equal(new[] { firstCollectionId, missingCollectionId }, profiles.RequestedCollectionIds);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task Rejects_incompatible_profiles_before_embedding_or_searching()
    {
        var firstCollectionId = Guid.NewGuid();
        var secondCollectionId = Guid.NewGuid();
        var profiles = new RecordingProfiles
        {
            Profiles =
            {
                [firstCollectionId] = new EmbeddingProfile("ollama", "test:1", "1", 3),
                [secondCollectionId] = new EmbeddingProfile("ollama", "test:2", "2", 3),
            },
        };
        var provider = new RecordingEmbeddingProvider();
        var repository = new RecordingRepository();
        var handler = new SemanticRetrievalHandler(profiles, provider, repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new SemanticRetrievalQuery([firstCollectionId, secondCollectionId], "query", 1)));

        Assert.Equal(new[] { firstCollectionId, secondCollectionId }, profiles.RequestedCollectionIds);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task Embeds_once_with_the_shared_persisted_profile_then_searches()
    {
        var firstCollectionId = Guid.NewGuid();
        var secondCollectionId = Guid.NewGuid();
        var profile = new EmbeddingProfile("ollama", "test:1", "1", 3);
        var profiles = new RecordingProfiles
        {
            Profiles =
            {
                [firstCollectionId] = profile,
                [secondCollectionId] = profile,
            },
        };
        var provider = new RecordingEmbeddingProvider();
        var expected = new SemanticRetrievalMatch(firstCollectionId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "matching text", 0);
        var repository = new RecordingRepository { Results = [expected] };
        var handler = new SemanticRetrievalHandler(profiles, provider, repository);

        var result = await handler.HandleAsync(new SemanticRetrievalQuery([firstCollectionId, secondCollectionId], "query", 2));

        Assert.Equal([expected], result);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(profile, provider.Profile);
        Assert.Equal(new[] { "query" }, provider.Inputs);
        Assert.Equal(new[] { firstCollectionId, secondCollectionId }, repository.CollectionIds);
        Assert.Equal(new[] { 1f, 2f, 3f }, repository.QueryEmbedding);
        Assert.Equal(2, repository.TopK);
    }

    [Fact]
    public async Task Loads_multi_collection_profiles_without_concurrent_repository_calls()
    {
        var firstCollectionId = Guid.NewGuid();
        var secondCollectionId = Guid.NewGuid();
        var profile = new EmbeddingProfile("ollama", "test:1", "1", 3);
        var profiles = new SingleAccessProfiles(firstCollectionId, secondCollectionId, profile);
        var provider = new RecordingEmbeddingProvider();
        var repository = new RecordingRepository();
        var handler = new SemanticRetrievalHandler(profiles, provider, repository);

        var handling = handler.HandleAsync(new SemanticRetrievalQuery([firstCollectionId, secondCollectionId], "query", 1));
        await profiles.FirstLookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([firstCollectionId], profiles.RequestedCollectionIds);
        Assert.Equal(1, profiles.MaximumConcurrentCalls);

        profiles.ReleaseFirstLookup();
        await handling;

        Assert.Equal(new[] { firstCollectionId, secondCollectionId }, profiles.RequestedCollectionIds);
        Assert.Equal(1, profiles.MaximumConcurrentCalls);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, repository.CallCount);
    }

    private sealed class RecordingProfiles : ICollectionEmbeddingProfileRepository
    {
        public Dictionary<Guid, EmbeddingProfile> Profiles { get; } = [];

        public List<Guid> RequestedCollectionIds { get; } = [];

        public Task<EmbeddingProfile?> GetProfileAsync(Guid collectionId, CancellationToken cancellationToken)
        {
            RequestedCollectionIds.Add(collectionId);
            return Task.FromResult(Profiles.GetValueOrDefault(collectionId));
        }
    }

    private sealed class SingleAccessProfiles(
        Guid firstCollectionId,
        Guid secondCollectionId,
        EmbeddingProfile profile) : ICollectionEmbeddingProfileRepository
    {
        private readonly TaskCompletionSource firstLookupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstLookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int maximumConcurrentCalls;

        public TaskCompletionSource FirstLookupStarted => firstLookupStarted;

        public List<Guid> RequestedCollectionIds { get; } = [];

        public int MaximumConcurrentCalls => maximumConcurrentCalls;

        public void ReleaseFirstLookup() => releaseFirstLookup.SetResult();

        public async Task<EmbeddingProfile?> GetProfileAsync(Guid collectionId, CancellationToken cancellationToken)
        {
            RequestedCollectionIds.Add(collectionId);
            var activeCalls = Interlocked.Increment(ref this.activeCalls);
            maximumConcurrentCalls = Math.Max(maximumConcurrentCalls, activeCalls);
            try
            {
                if (activeCalls > 1)
                {
                    throw new InvalidOperationException("Profile lookups must not overlap.");
                }

                if (collectionId == firstCollectionId)
                {
                    firstLookupStarted.SetResult();
                    await releaseFirstLookup.Task.WaitAsync(cancellationToken);
                }

                return collectionId == firstCollectionId || collectionId == secondCollectionId ? profile : null;
            }
            finally
            {
                Interlocked.Decrement(ref this.activeCalls);
            }
        }
    }

    private sealed class RecordingEmbeddingProvider : IEmbeddingProvider
    {
        public int CallCount { get; private set; }

        public EmbeddingProfile? Profile { get; private set; }

        public IReadOnlyList<string>? Inputs { get; private set; }

        public Task<EmbeddingResponse> EmbedAsync(EmbeddingProfile profile, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
        {
            CallCount++;
            Profile = profile;
            Inputs = inputs;
            return Task.FromResult(new EmbeddingResponse([new float[] { 1, 2, 3 }]));
        }
    }

    private sealed class RecordingRepository : ISemanticRetrievalRepository
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<Guid>? CollectionIds { get; private set; }

        public float[]? QueryEmbedding { get; private set; }

        public int TopK { get; private set; }

        public IReadOnlyList<SemanticRetrievalMatch> Results { get; init; } = [];

        public Task<IReadOnlyList<SemanticRetrievalMatch>> SearchAsync(
            IReadOnlyList<Guid> collectionIds,
            float[] queryEmbedding,
            int topK,
            CancellationToken cancellationToken)
        {
            CallCount++;
            CollectionIds = collectionIds;
            QueryEmbedding = queryEmbedding;
            TopK = topK;
            return Task.FromResult(Results);
        }
    }
}
