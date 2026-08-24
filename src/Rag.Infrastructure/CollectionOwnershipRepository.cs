using Npgsql;
using Rag.Application;

namespace Rag.Infrastructure;

public sealed class CollectionOwnershipRepository(NpgsqlDataSource dataSource) : ICollectionOwnershipRepository
{
    public async Task<IReadOnlyList<UnownedCollection>> ListUnownedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "Id", "Name", "CreatedAt"
            FROM collections
            WHERE "ServiceClientId" IS NULL
            ORDER BY "CreatedAt", "Id";
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var collections = new List<UnownedCollection>();
        while (await reader.ReadAsync(cancellationToken))
        {
            collections.Add(new UnownedCollection(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return collections;
    }

    public async Task AssignOwnerAsync(Guid collectionId, Guid serviceClientId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE collections
            SET "ServiceClientId" = @serviceClientId
            WHERE "Id" = @collectionId
              AND "ServiceClientId" IS NULL
              AND EXISTS (SELECT 1 FROM service_clients WHERE "Id" = @serviceClientId);
            """;
        command.Parameters.AddWithValue("collectionId", collectionId);
        command.Parameters.AddWithValue("serviceClientId", serviceClientId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The collection is already owned, does not exist, or the service client does not exist.");
        }
    }
}
