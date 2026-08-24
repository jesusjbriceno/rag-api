using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.Application;
using Rag.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
using var host = builder.Build();

if (args.Length is 0 or > 3)
{
    return Usage();
}

try
{
    using var scope = host.Services.CreateScope();
    var credentials = scope.ServiceProvider.GetRequiredService<CredentialOperator>();
    switch (args[0].ToLowerInvariant())
    {
        case "issue" when args.Length == 2:
            {
                var issued = await credentials.IssueAsync(args[1], expiresAt: null);
                WriteCredential(issued);
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
    Console.WriteLine($"Secret: {issued.Secret}");
    Console.WriteLine("Store the secret securely. It cannot be displayed again.");
}

static int Usage()
{
    Console.Error.WriteLine("Usage: Rag.Operator issue <service-client-name> | rotate <key-id> | revoke <key-id>");
    return 2;
}
