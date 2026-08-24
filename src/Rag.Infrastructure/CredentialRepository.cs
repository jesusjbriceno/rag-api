using Microsoft.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class CredentialRepository(IngestionDbContext context) : ICredentialRepository, ICredentialStateValidator
{
    public Task<ClientCredential?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken) =>
        context.ClientCredentials.SingleOrDefaultAsync(credential => credential.KeyId == keyId, cancellationToken);

    public Task<ClientCredential?> FindByIdAsync(Guid credentialId, CancellationToken cancellationToken) =>
        context.ClientCredentials.SingleOrDefaultAsync(credential => credential.Id == credentialId, cancellationToken);

    public Task<ServiceClient?> FindServiceClientByNameAsync(string name, CancellationToken cancellationToken) =>
        context.ServiceClients.SingleOrDefaultAsync(client => client.Name == name, cancellationToken);

    public void Add(ServiceClient serviceClient, ClientCredential credential)
    {
        context.ServiceClients.Add(serviceClient);
        context.ClientCredentials.Add(credential);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            context.ChangeTracker.Clear();
            throw new CredentialConcurrencyException("The credential was modified by another operation.", exception);
        }
    }

    public async Task<bool> IsCurrentAsync(CredentialIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var credential = await context.ClientCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == identity.CredentialId, cancellationToken);
        return credential is not null &&
            credential.ServiceClientId == identity.ServiceClientId &&
            credential.Version == identity.Version &&
            credential.IsActiveAt(now);
    }
}
