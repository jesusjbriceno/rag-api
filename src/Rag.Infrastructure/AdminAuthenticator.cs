using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class AdminAuthenticator(
    AdminMachineProofVerifier proofVerifier,
    AdminAssertionValidator assertionValidator,
    IAdminAssertionReplayRepository replayRepository)
{
    public async Task<AdminActor?> AuthenticateAsync(
        AdminMachineProof proof,
        string signature,
        string assertionJws,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!proofVerifier.Verify(proof, signature, now))
        {
            return null;
        }

        var assertion = assertionValidator.Validate(assertionJws, now);
        if (assertion is null || !string.Equals(assertion.AppId, proof.AppId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!await replayRepository.ReserveAsync(
                assertion.Issuer,
                assertion.Jti,
                assertion.AppId,
                assertion.ExpiresAt,
                cancellationToken))
        {
            return null;
        }

        return new AdminActor(assertion.Subject, assertion.AppId);
    }
}
