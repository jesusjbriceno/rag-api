using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Rag.Api;

/// <summary>
/// Configures the generated OpenAPI document so it states the contract the middleware actually enforces:
/// the executing version, the three authorization planes, and each operation's real security requirements.
/// </summary>
public static class OpenApiDocumentContract
{
    public const string Public = "Public";

    public const string Admin = "Admin";

    public const string Historical = "Historical";

    public const string BearerSchemeId = "bearerAuth";

    public const string AdminPolicy = "AdminPlane";

    public const string HistoricalUploadsWritePolicy = "HistoricalUploadsWrite";

    public const string HistoricalOperationsReadPolicy = "HistoricalOperationsRead";

    public const string UploadsWriteScope = "historical:uploads.write";

    public const string OperationsReadScope = "historical:operations.read";

    /// <summary>The six signed headers the admin plane requires, in canonical-string order.</summary>
    public static readonly string[] AdminHeaderSchemes =
    [
        "X-Admin-App-Id",
        "X-Admin-Key-Id",
        "X-Admin-Timestamp",
        "X-Admin-Signature",
        "X-Admin-Assertion",
        "Idempotency-Key",
    ];

    private const string AdminCanonicalString =
        "METHOD\nPathAndQuery\nBodyHash\nAssertionHash\nAppId\nKeyId\nTimestampUnixSeconds\nIdempotencyKey";

    private const string ContractDescription =
        "Routes are grouped in three authorization planes. The public plane authenticates with a bearer JWT " +
        "issued by POST /api/v1/auth/token and rejects tokens that carry a scope claim. The historical plane " +
        "is configuration-gated and off by default (HistoricalIngestion:Enabled=false); when enabled it " +
        "requires a bearer JWT carrying the exact scope historical:uploads.write or " +
        "historical:operations.read. The admin plane is configuration-gated (AdminPlane:Enabled) and " +
        "authenticates machine callers with six headers carrying an HMAC-SHA256 proof and an RS256 JWS " +
        "assertion. The health endpoints /api/v1/health, /api/v1/health/live and /api/v1/health/ready are " +
        "anonymous: /api/v1/health/live and /api/v1/health/ready answer plain text rather than JSON " +
        "(200 healthy, 503 not ready), and /api/v1/health reports the same status as a JSON document.";

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer(DeclareDocumentContract);
        options.AddOperationTransformer(DeclareOperationSecurity);
    }

    private static Task DeclareDocumentContract(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Version = ResolveInformationalVersion();
        document.Info.Description = ContractDescription;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[BearerSchemeId] = BearerScheme();
        foreach (var header in AdminHeaderSchemes)
        {
            document.Components.SecuritySchemes[header] = AdminHeaderScheme(header);
        }

        return Task.CompletedTask;
    }

    private static Task DeclareOperationSecurity(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = EndpointMetadata(context);
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            // No requirement at all: omit the property so the operation does not inherit a global scheme.
            operation.Security = null;
            return Task.CompletedTask;
        }

        var policy = metadata.OfType<IAuthorizeData>()
            .Select(authorize => authorize.Policy)
            .FirstOrDefault(policy => !string.IsNullOrEmpty(policy));

        switch (policy)
        {
            case AdminPolicy:
                operation.Security = [AdminRequirement(context.Document)];
                break;
            case HistoricalUploadsWritePolicy:
                RequireBearer(operation, context.Document, UploadsWriteScope);
                break;
            case HistoricalOperationsReadPolicy:
                RequireBearer(operation, context.Document, OperationsReadScope);
                break;
            default:
                RequireBearer(operation, context.Document, scope: null);
                break;
        }

        EnsureAuthenticationResponses(operation);
        return Task.CompletedTask;
    }

    private static IEnumerable<object> EndpointMetadata(OpenApiOperationTransformerContext context) =>
        context.Description.ActionDescriptor?.EndpointMetadata ?? [];

    private static void RequireBearer(OpenApiOperation operation, OpenApiDocument? document, string? scope)
    {
        operation.Security = [BearerRequirement(document)];
        operation.Description = scope is null
            ? Join(operation.Description, "Requires a bearer JWT. Public routes reject tokens that carry a scope claim.")
            : Join(operation.Description, $"Requires a bearer JWT carrying the scope {scope}.");
    }

    private static OpenApiSecurityRequirement BearerRequirement(OpenApiDocument? document) =>
        new()
        {
            { new OpenApiSecuritySchemeReference(BearerSchemeId, document), [] },
        };

    private static OpenApiSecurityRequirement AdminRequirement(OpenApiDocument? document)
    {
        // One requirement object means AND: every admin header scheme is required.
        var requirement = new OpenApiSecurityRequirement();
        foreach (var header in AdminHeaderSchemes)
        {
            requirement.Add(new OpenApiSecuritySchemeReference(header, document), []);
        }

        return requirement;
    }

    private static void EnsureAuthenticationResponses(OpenApiOperation operation)
    {
        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("401", new OpenApiResponse
        {
            Description = "Missing or invalid credentials.",
        });
        operation.Responses.TryAdd("403", new OpenApiResponse
        {
            Description = "Credentials are valid but lack the required policy or scope.",
        });
    }

    private static OpenApiSecurityScheme BearerScheme() => new()
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description =
            "Bearer JWT issued by POST /api/v1/auth/token. Public routes require a token without a scope " +
            "claim; the historical routes require a token carrying the exact scope stated on the operation.",
    };

    private static OpenApiSecurityScheme AdminHeaderScheme(string header) => new()
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = header,
        Description = AdminHeaderDescription(header),
    };

    private static string AdminHeaderDescription(string header) => header switch
    {
        "X-Admin-App-Id" => $"Admin plane application id, covered by the proof canonical string {AdminCanonicalString}.",
        "X-Admin-Key-Id" => $"Admin plane key id selecting the HMAC secret, covered by the proof canonical string {AdminCanonicalString}.",
        "X-Admin-Timestamp" => $"Admin plane timestamp in Unix seconds, covered by the proof canonical string {AdminCanonicalString}.",
        "X-Admin-Signature" => $"HMAC-SHA256 proof over the canonical string {AdminCanonicalString}.",
        "X-Admin-Assertion" => $"RS256 JWS assertion identifying the caller; its hash is covered by the canonical string {AdminCanonicalString}.",
        "Idempotency-Key" => $"Idempotency key of the admin request, covered by the proof canonical string {AdminCanonicalString}.",
        _ => "Admin plane credential header.",
    };

    private static string ResolveInformationalVersion() =>
        typeof(OpenApiDocumentContract).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private static string Join(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";
}
