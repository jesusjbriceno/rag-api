using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminPlaneModelTests
{
    [Fact]
    public void Admin_entities_map_to_their_expected_tables_columns_and_indexes()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql("Host=localhost;Database=model;Username=model;Password=model", provider => provider.UseVector())
            .Options;
        using var context = new IngestionDbContext(options);
        var model = context.Model;

        var audit = model.FindEntityType(typeof(AdminAuditEvent))!;
        Assert.Equal("admin_audit_events", audit.GetTableName());
        Assert.False(audit.FindProperty(nameof(AdminAuditEvent.ActorSubject))!.IsNullable);
        Assert.False(audit.FindProperty(nameof(AdminAuditEvent.AppId))!.IsNullable);
        Assert.False(audit.FindProperty(nameof(AdminAuditEvent.OccurredAt))!.IsNullable);
        Assert.True(audit.FindProperty(nameof(AdminAuditEvent.AllowlistedJson))!.IsNullable);
        Assert.Contains(audit.GetIndexes(), index => index.Properties.Any(property => property.Name == nameof(AdminAuditEvent.OccurredAt)) && index.Properties.Any(property => property.Name == nameof(AdminAuditEvent.Id)) && !index.IsUnique);

        var operation = model.FindEntityType(typeof(AdminOperation))!;
        Assert.Equal("admin_operations", operation.GetTableName());
        var operationIdempotency = operation.GetIndexes().Single(index =>
            index.Properties.Any(property => property.Name == nameof(AdminOperation.AppId)) &&
            index.Properties.Any(property => property.Name == nameof(AdminOperation.IdempotencyKey)));
        Assert.True(operationIdempotency.IsUnique);

        var replay = model.FindEntityType(typeof(AdminAssertionReplay))!;
        Assert.Equal("admin_assertion_replays", replay.GetTableName());
        var replayKey = replay.GetIndexes().Single(index =>
            index.Properties.Any(property => property.Name == nameof(AdminAssertionReplay.Issuer)) &&
            index.Properties.Any(property => property.Name == nameof(AdminAssertionReplay.Jti)));
        Assert.True(replayKey.IsUnique);
        Assert.Contains(replay.GetIndexes(), index => index.Properties.Any(property => property.Name == nameof(AdminAssertionReplay.ExpiresAt)));
    }

    [Fact]
    public void Client_metadata_columns_are_nullable_domain_extensions()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql("Host=localhost;Database=model;Username=model;Password=model", provider => provider.UseVector())
            .Options;
        using var context = new IngestionDbContext(options);
        var model = context.Model;

        var client = model.FindEntityType(typeof(ServiceClient))!;
        Assert.True(client.FindProperty(nameof(ServiceClient.Description))!.IsNullable);

        var credential = model.FindEntityType(typeof(ClientCredential))!;
        Assert.True(credential.FindProperty(nameof(ClientCredential.Description))!.IsNullable);
        Assert.True(credential.FindProperty(nameof(ClientCredential.LastRotatedAt))!.IsNullable);
    }
}
