using Microsoft.AspNetCore.Http;

namespace Rag.Api;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin")
            .RequireAuthorization("AdminPlane");

        group.MapAdminClientEndpoints();
        group.MapAdminCredentialReadEndpoints();
        group.MapAdminCredentialMutationEndpoints();
        group.MapAdminAuditEndpoints();

        return group;
    }
}
