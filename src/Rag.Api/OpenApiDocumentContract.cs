using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    private const string ProblemContentType = "application/problem+json";

    private const string ProblemSchemaId = "ProblemDetails";

    /// <summary>The three plane tags the document declares, in the order they describe the contract.</summary>
    private static readonly (string Name, string Description)[] PlaneTagDescriptions =
    [
        (Public,
            "Routes authenticated with a bearer JWT issued by POST /api/v1/auth/token, plus the anonymous " +
            "health and token endpoints."),
        (Admin,
            "Configuration-gated machine-to-machine routes (AdminPlane:Enabled) authenticated with the six " +
            "admin headers carrying an HMAC-SHA256 proof and an RS256 JWS assertion."),
        (Historical,
            "Configuration-gated historical ingestion and telemetry routes (HistoricalIngestion:Enabled) " +
            "requiring a bearer JWT carrying the exact operation scope."),
    ];

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

    /// <summary>
    /// Declares the accepted body for the generated document only. <c>Accepts&lt;T&gt;</c> is <c>IAcceptsMetadata</c>,
    /// and RequestDelegateFactory enforces the content type before the handler runs, which would replace these
    /// endpoints' own problem+json 415 with a bodyless one. The generation pass declares the body; production keeps
    /// its 415 contract.
    /// </summary>
    internal static RouteHandlerBuilder DeclaresBody<TRequest>(this RouteHandlerBuilder builder, string contentType, bool openApiGeneration) =>
        openApiGeneration ? builder.Accepts<TRequest>(contentType) : builder;

    /// <summary>
    /// A non-success response the handler can produce, for the generated document only. It carries the description
    /// the operation transformer attaches to the response the matching <c>Produces</c> call already created.
    /// </summary>
    internal sealed record ProblemResponseMetadata(int StatusCode, string Description);

    /// <summary>
    /// Declares a non-success response for the generated document only. A response with a body is the handler's own
    /// RFC 7807 problem (<c>application/problem+json</c>); the bodyless form covers responses the framework produces
    /// before the handler runs, such as a wrong content type on a framework-bound body. Neither form changes runtime
    /// behaviour.
    /// </summary>
    internal static RouteHandlerBuilder DeclaresProblem(
        this RouteHandlerBuilder builder,
        int statusCode,
        string description,
        bool hasBody = true)
    {
        if (hasBody)
        {
            builder.Produces<ProblemDetails>(statusCode, ProblemContentType);
        }
        else
        {
            builder.Produces(statusCode);
        }

        builder.WithMetadata(new ProblemResponseMetadata(statusCode, description));
        return builder;
    }

    /// <summary>
    /// Declares a request header the handler reads itself, for the generated document only. The handler binds the
    /// value manually (for example through <c>AdminEndpointSupport.TryParseIfMatch</c>), so there is no endpoint
    /// parameter for the generator to discover.
    /// </summary>
    internal sealed record HeaderParameterMetadata(string Name, bool Required, string Description);

    internal static RouteHandlerBuilder DeclaresHeader(
        this RouteHandlerBuilder builder,
        string name,
        bool required,
        string description)
    {
        builder.WithMetadata(new HeaderParameterMetadata(name, required, description));
        return builder;
    }

    /// <summary>
    /// Resolves the generation flag for mapping code that cannot receive it as a parameter. It reports the same host
    /// environment the application checks at startup, so the document and the endpoints always agree.
    /// </summary>
    internal static bool IsOpenApiGeneration(this IEndpointRouteBuilder endpoints) =>
        endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>()
            .IsEnvironment(ApiEndpointSupport.OpenApiGenerationEnvironment);

    private static Task DeclareDocumentContract(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Version = ResolveInformationalVersion();
        document.Info.Description = ContractDescription;

        // The generator tags each operation with its declaring class; the document declares the three
        // authorization planes instead, and every operation carries exactly one of them.
        document.Tags = new HashSet<OpenApiTag>();
        foreach (var (name, description) in PlaneTagDescriptions)
        {
            document.Tags.Add(new OpenApiTag { Name = name, Description = description });
        }

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
        var anonymous = metadata.OfType<IAllowAnonymous>().Any();
        var policy = anonymous
            ? null
            : metadata.OfType<IAuthorizeData>()
                .Select(authorize => authorize.Policy)
                .FirstOrDefault(policy => !string.IsNullOrEmpty(policy));

        // Exactly one plane tag, assigned rather than appended so the tag the generator infers from the
        // declaring class disappears. The plane is the same decision that selects the security below.
        operation.Tags = new HashSet<OpenApiTagReference> { new(PlaneTag(policy), context.Document) };

        DeclareProblemDescriptions(operation, metadata, policy == AdminPolicy);
        DeclareHeaderParameters(operation, metadata);
        DeclareInternalServerError(operation, context.Document, policy == AdminPolicy);

        if (anonymous)
        {
            // No requirement at all: omit the property so the operation does not inherit a global scheme.
            operation.Security = null;
            return Task.CompletedTask;
        }

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

    private static void DeclareProblemDescriptions(OpenApiOperation operation, IEnumerable<object> metadata, bool admin)
    {
        const string adminNote =
            " The admin plane answers application/problem+json with the code extension and, when present, traceId.";

        foreach (var problem in metadata.OfType<ProblemResponseMetadata>())
        {
            if (operation.Responses?.TryGetValue(problem.StatusCode.ToString(CultureInfo.InvariantCulture), out var response) == true &&
                response is OpenApiResponse concrete)
            {
                concrete.Description = admin ? problem.Description + adminNote : problem.Description;
            }
        }
    }

    private static void DeclareHeaderParameters(OpenApiOperation operation, IEnumerable<object> metadata)
    {
        foreach (var header in metadata.OfType<HeaderParameterMetadata>())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = header.Name,
                In = ParameterLocation.Header,
                Required = header.Required,
                Description = header.Description,
            });
        }
    }

    private static void DeclareInternalServerError(OpenApiOperation operation, OpenApiDocument? document, bool admin)
    {
        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("500", new OpenApiResponse
        {
            Description = admin
                ? "Internal server error. The global exception handler answers application/problem+json titled " +
                  "\"Internal server error\"; the admin plane also carries the code extension and, when present, traceId."
                : "Internal server error. The global exception handler answers application/problem+json titled " +
                  "\"Internal server error\".",
            Content = ProblemContent(document),
        });
    }

    private static Dictionary<string, OpenApiMediaType> ProblemContent(OpenApiDocument? document) =>
        new(StringComparer.Ordinal)
        {
            [ProblemContentType] = new OpenApiMediaType
            {
                Schema = new OpenApiSchemaReference(ProblemSchemaId, document),
            },
        };

    private static string PlaneTag(string? policy) => policy switch
    {
        AdminPolicy => Admin,
        HistoricalUploadsWritePolicy or HistoricalOperationsReadPolicy => Historical,
        _ => Public,
    };

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
