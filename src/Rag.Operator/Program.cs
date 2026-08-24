using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Rag.Application;
using Rag.Infrastructure;

if (args.Length is 0 or > 4)
{
    return Usage();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
using var host = builder.Build();

try
{
    using var scope = host.Services.CreateScope();
    var credentials = scope.ServiceProvider.GetRequiredService<CredentialOperator>();
    var collections = scope.ServiceProvider.GetRequiredService<CollectionOwnershipOperator>();
    switch (args[0].ToLowerInvariant())
    {
        case "issue" when args.Length == 2:
            {
                var issued = await credentials.IssueAsync(args[1], expiresAt: null);
                WriteCredential(issued);
                break;
            }
        case "migrate" when args.Length == 1:
            {
                var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<IngestionDbContext>>();
                await using var dbContext = await contextFactory.CreateDbContextAsync();
                await dbContext.Database.MigrateAsync();
                Console.WriteLine("Database migrations applied.");
                break;
            }
        case "rotate" when args.Length == 2:
            {
                var issued = await credentials.RotateAsync(args[1]);
                WriteCredential(issued);
                break;
            }
        case "revoke" when args.Length == 2:
            await credentials.RevokeAsync(args[1]);
            Console.WriteLine("Credential revoked.");
            break;
        case "collections" when args.Length == 2 && string.Equals(args[1], "list-unowned", StringComparison.OrdinalIgnoreCase):
            {
                var unowned = await collections.ListUnownedAsync();
                foreach (var collection in unowned)
                {
                    Console.WriteLine($"{collection.Id:D}\t{collection.Name}");
                }

                break;
            }
        case "collections" when args.Length == 4 && string.Equals(args[1], "assign-owner", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(args[2], out var collectionId) && Guid.TryParse(args[3], out var serviceClientId):
            await collections.AssignOwnerAsync(collectionId, serviceClientId);
            Console.WriteLine("Collection owner assigned.");
            break;
        default:
            return Usage();
    }
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

return 0;

static void WriteCredential(IssuedCredential issued)
{
    Console.WriteLine($"KeyId: {issued.KeyId}");
    Console.WriteLine($"ServiceClientId: {issued.ServiceClientId:D}");
    Console.WriteLine($"Secret: {issued.Secret}");
    Console.WriteLine("Store the secret securely. It cannot be displayed again.");
}

static int Usage()
{
    Console.Error.WriteLine("Usage: Rag.Operator migrate | issue <service-client-name> | rotate <key-id> | revoke <key-id>");
    Console.Error.WriteLine("       Rag.Operator collections list-unowned | collections assign-owner <collection-id> <service-client-id>");
    Console.Error.WriteLine("Ownership migration: apply the preparatory migration, list unowned collections, assign every collection deliberately, then rerun the enforcement migration.");
    Console.Error.WriteLine("The assignment command never creates an owner or reassigns an owned collection.");
    return 2;
}
