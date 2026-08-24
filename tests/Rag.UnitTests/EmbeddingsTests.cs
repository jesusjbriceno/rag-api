using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class EmbeddingProfileTests
{
    [Fact]
    public void Rejects_latest_as_a_persisted_model_or_version()
    {
        Assert.Throws<ArgumentException>(() => new EmbeddingProfile("ollama", "qwen3-embedding:latest", "0.6b", 1_024));
        Assert.Throws<ArgumentException>(() => new EmbeddingProfile("ollama", "qwen3-embedding:0.6b", "latest", 1_024));
    }

    [Fact]
    public void Requires_the_default_profile_in_the_allow_list()
    {
        var options = new EmbeddingOptions
        {
            AllowedProfiles = [new EmbeddingProfileOptions { Provider = "ollama", Model = "other:1", Version = "1", Dimensions = 3 }],
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}

public sealed class OllamaEmbeddingProviderTests
{
    [Fact]
    public async Task Sends_an_openai_compatible_request_and_validates_the_ordered_response()
    {
        string? requestBody = null;
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/embeddings", request.RequestUri!.AbsolutePath);
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(new
            {
                data = new[]
                {
                    new { index = 0, embedding = new float[] { 1, 2, 3 } },
                    new { index = 1, embedding = new float[] { 4, 5, 6 } },
                },
            });
        });
        var profile = new EmbeddingProfile("ollama", "test:1", "1", 3);
        var provider = new OllamaEmbeddingProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            Options.Create(OptionsFor(profile)));

        var response = await provider.EmbedAsync(profile, ["first", "second"], CancellationToken.None);

        Assert.Equal(new[] { 1f, 2f, 3f }, response.Vectors[0]);
        Assert.Equal(new[] { 4f, 5f, 6f }, response.Vectors[1]);
        using var request = JsonDocument.Parse(requestBody!);
        Assert.Equal("test:1", request.RootElement.GetProperty("model").GetString());
        Assert.Equal(3, request.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("first", request.RootElement.GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task Rejects_response_with_missing_or_wrong_dimension_vectors()
    {
        var profile = new EmbeddingProfile("ollama", "test:1", "1", 3);
        var handler = new DelegateHandler(_ => Task.FromResult(JsonResponse(new
        {
            data = new[]
            {
                new { index = 1, embedding = new float[] { 1, 2, 3 } },
                new { index = 0, embedding = new float[] { 4, 5, 6 } },
            },
        })));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var provider = new OllamaEmbeddingProvider(client, Options.Create(OptionsFor(profile)));

        await Assert.ThrowsAsync<EmbeddingProviderException>(() => provider.EmbedAsync(profile, ["first", "second"], CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_response_with_a_vector_of_the_wrong_dimension()
    {
        var profile = new EmbeddingProfile("ollama", "test:1", "1", 3);
        var handler = new DelegateHandler(_ => Task.FromResult(JsonResponse(new
        {
            data = new[] { new { index = 0, embedding = new float[] { 1, 2 } } },
        })));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var provider = new OllamaEmbeddingProvider(client, Options.Create(OptionsFor(profile)));

        await Assert.ThrowsAsync<EmbeddingProviderException>(() => provider.EmbedAsync(profile, ["first"], CancellationToken.None));
    }

    private static EmbeddingOptions OptionsFor(EmbeddingProfile profile) => new()
    {
        Default = EmbeddingProfileOptions.FromProfile(profile),
        AllowedProfiles = [EmbeddingProfileOptions.FromProfile(profile)],
    };

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

public sealed class QueryEmbeddingServiceTests
{
    [Fact]
    public async Task Uses_the_exact_profile_persisted_for_the_collection()
    {
        var profile = new EmbeddingProfile("ollama", "custom:1", "1", 3);
        var provider = new RecordingEmbeddingProvider();
        var service = new QueryEmbeddingService(new StaticCollectionProfiles(profile), provider);

        var result = await service.EmbedAsync(Guid.NewGuid(), Guid.NewGuid(), "query");

        Assert.Equal(profile, provider.Profile);
        Assert.Equal(new[] { 1f, 2f, 3f }, result);
    }

    private sealed class StaticCollectionProfiles(EmbeddingProfile profile) : ICollectionEmbeddingProfileRepository
    {
        public Task<EmbeddingProfile?> GetProfileAsync(Guid serviceClientId, Guid collectionId, CancellationToken cancellationToken) => Task.FromResult<EmbeddingProfile?>(profile);
    }

    private sealed class RecordingEmbeddingProvider : IEmbeddingProvider
    {
        public EmbeddingProfile? Profile { get; private set; }

        public Task<EmbeddingResponse> EmbedAsync(EmbeddingProfile profile, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
        {
            Profile = profile;
            return Task.FromResult(new EmbeddingResponse([new float[] { 1, 2, 3 }]));
        }
    }
}
