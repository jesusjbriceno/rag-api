using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

// Reason categories for admin authentication failures. Surfaced to observability
// so metrics/logs can distinguish failure classes without ever exposing
// credentials, assertions, headers, or bodies. Only the enum name is logged.
public enum AdminAuthFailureReason
{
    None = 0,
    ForbiddenIdentityHeader,
    IncompleteHeaders,
    BodyTooLarge,
    MachineProofInvalid,
    AssertionInvalid,
    AppMismatch,
    ReplayRejected,
}

public readonly record struct AdminAuthenticationResult(AdminActor? Actor, AdminAuthFailureReason FailureReason)
{
    public bool Succeeded => Actor is not null;
}

public sealed class AdminAuthenticator(
    AdminMachineProofVerifier proofVerifier,
    AdminAssertionValidator assertionValidator,
    IAdminAssertionReplayRepository replayRepository)
{
    public async Task<AdminAuthenticationResult> AuthenticateAsync(
        AdminMachineProof proof,
        string signature,
        string assertionJws,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!proofVerifier.Verify(proof, signature, now))
        {
            return new AdminAuthenticationResult(null, AdminAuthFailureReason.MachineProofInvalid);
        }

        var assertion = assertionValidator.Validate(assertionJws, now);
        if (assertion is null)
        {
            return new AdminAuthenticationResult(null, AdminAuthFailureReason.AssertionInvalid);
        }

        if (!string.Equals(assertion.AppId, proof.AppId, StringComparison.Ordinal))
        {
            return new AdminAuthenticationResult(null, AdminAuthFailureReason.AppMismatch);
        }

        if (!await replayRepository.ReserveAsync(
                assertion.Issuer,
                assertion.Jti,
                assertion.AppId,
                assertion.ExpiresAt,
                cancellationToken))
        {
            return new AdminAuthenticationResult(null, AdminAuthFailureReason.ReplayRejected);
        }

        return new AdminAuthenticationResult(new AdminActor(assertion.Subject, assertion.AppId), AdminAuthFailureReason.None);
    }
}
