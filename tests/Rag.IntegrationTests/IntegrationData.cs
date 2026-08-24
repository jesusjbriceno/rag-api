using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

internal static class IntegrationData
{
    public static Collection NewCollection(
        IngestionDbContext context,
        string name,
        DateTimeOffset createdAt,
        EmbeddingProfile? profile = null)
    {
        var client = new ServiceClient(Guid.NewGuid(), $"test-client-{Guid.NewGuid():N}", createdAt);
        context.ServiceClients.Add(client);
        return new Collection(Guid.NewGuid(), client.Id, name, createdAt, profile ?? EmbeddingProfile.Default);
    }

    public static Collection NewCollection(
        IngestionDbContext context,
        Guid id,
        string name,
        DateTimeOffset createdAt,
        EmbeddingProfile profile)
    {
        var client = new ServiceClient(Guid.NewGuid(), $"test-client-{Guid.NewGuid():N}", createdAt);
        context.ServiceClients.Add(client);
        return new Collection(id, client.Id, name, createdAt, profile);
    }
}
