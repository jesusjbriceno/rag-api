using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Rag.Api;
using Rag.Api.Historical;
using Rag.Application;
using Rag.Application.Auth;
using Rag.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(OpenApiDocumentContract.Configure);

var openApiGeneration = builder.Environment.IsEnvironment(ApiEndpointSupport.OpenApiGenerationEnvironment);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, openApiGeneration);
builder.Services.AddHistoricalIngestion();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        var keyMaterial = new JwtKeyMaterial(jwtOptions);
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeyResolver = (_, _, keyId, _) =>
                keyId is not null && keyMaterial.ValidationKeys.TryGetValue(keyId, out var key) ? [key] : [],
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized").ExecuteAsync(context.HttpContext);
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden").ExecuteAsync(context.HttpContext);
            },
            OnTokenValidated = async context =>
            {
                var credentialId = context.Principal?.FindFirst("credential_id")?.Value;
                var clientId = context.Principal?.FindFirst("client_id")?.Value;
                var version = context.Principal?.FindFirst("credential_version")?.Value;
                if (!Guid.TryParse(credentialId, out var parsedCredentialId) ||
                    !Guid.TryParse(clientId, out var parsedClientId) ||
                    !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion) ||
                    parsedVersion < 1)
                {
                    context.Fail("Credential claims are invalid.");
                    return;
                }

                var stateValidator = context.HttpContext.RequestServices.GetRequiredService<ICredentialStateValidator>();
                if (!await stateValidator.IsCurrentAsync(
                    new CredentialIdentity(parsedCredentialId, parsedClientId, parsedVersion),
                    DateTimeOffset.UtcNow,
                    context.HttpContext.RequestAborted))
                {
                    context.Fail("Credential is no longer active.");
                }
            },
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => context.User.Claims.All(claim => claim.Type != "scope"))
        .Build())
    .AddPolicy(HistoricalAuthorizationPolicies.UploadsWrite, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasHistoricalScope(context.User, HistoricalScopes.UploadsWrite)))
    .AddPolicy(HistoricalAuthorizationPolicies.OperationsRead, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasHistoricalScope(context.User, HistoricalScopes.OperationsRead)));
if (builder.Configuration.GetValue<bool>("AdminPlane:Enabled"))
{
    builder.Services.AddAuthentication()
        .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>(
            AdminAuthenticationDefaults.AuthenticationScheme, _ => { });
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("AdminPlane", policy => policy
            .AddAuthenticationSchemes(AdminAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser());
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("credential-exchange", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
    var historicalLimits = builder.Configuration.GetSection(HistoricalIngestionOptions.SectionName).Get<HistoricalIngestionOptions>() ?? new HistoricalIngestionOptions();
    options.AddFixedWindowLimiter(HistoricalRateLimitPolicies.Uploads, limiterOptions =>
    {
        limiterOptions.PermitLimit = historicalLimits.UploadsRateLimitPermit;
        limiterOptions.Window = historicalLimits.UploadsRateLimitWindow;
        limiterOptions.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Internal server error").ExecuteAsync(context)));
app.UseAuthentication();
app.UseRateLimiter();
if (builder.Configuration.GetValue<bool>("AdminPlane:Enabled"))
{
    app.UseMiddleware<AdminObservabilityMiddleware>();
}
app.UseAuthorization();

app.MapHealthChecks("/api/v1/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();
app.MapHealthChecks("/api/v1/health/ready", new HealthCheckOptions
{
    Predicate = healthCheck => healthCheck.Tags.Contains("ready"),
}).AllowAnonymous();
var informationalVersion = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "unknown";

app.MapGet("/api/v1/health", () => Results.Ok(new HealthResponse("healthy", informationalVersion)))
    .WithName("get_health")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    .AllowAnonymous();
app.MapPost("/api/v1/auth/token", async (TokenExchangeRequest request, CredentialExchangeHandler handler, CancellationToken cancellationToken) =>
    {
        var result = await handler.ExchangeScopedAsync(request.KeyId, request.Secret, request.Scope, cancellationToken);
        return result.Outcome switch
        {
            TokenExchangeOutcome.Unauthorized => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized"),
            TokenExchangeOutcome.InvalidScope => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_scope"),
            _ when result.Token!.Scope is null => Results.Ok(new TokenResponse(result.Token.Value, "Bearer", 900, null)),
            _ => Results.Ok(new TokenResponse(result.Token!.Value, "Bearer", 900, result.Token.Scope)),
        };
    })
    .WithName("exchange_token")
    .DeclaresBody<TokenExchangeRequest>("application/json", openApiGeneration)
    .Produces<TokenResponse>(StatusCodes.Status200OK)
    .DeclaresProblem(
        StatusCodes.Status400BadRequest,
        "Bad request. The handler answers application/problem+json titled \"invalid_scope\" when the requested " +
        "scope is unknown; the framework's bodyless rejection of a malformed or oversized request body uses the " +
        "same status with no response body.")
    .DeclaresProblem(
        StatusCodes.Status401Unauthorized,
        "Unauthorized. The credential key id is unknown or the secret does not match; the handler answers " +
        "application/problem+json titled \"Unauthorized\".")
    .DeclaresProblem(
        StatusCodes.Status415UnsupportedMediaType,
        "Unsupported content type. The framework binds the request body as application/json; any other content type " +
        "is rejected before the handler runs, with no response body.",
        hasBody: false)
    .DeclaresProblem(
        StatusCodes.Status429TooManyRequests,
        "Too many requests. The credential exchange is limited to five requests per minute per caller; the rate " +
        "limiter answers 429 with no response body.",
        hasBody: false)
    .AllowAnonymous()
    .RequireRateLimiting("credential-exchange");

app.MapPost("/api/v1/collections", async (HttpContext context, CreateCollectionHandler handler, CancellationToken cancellationToken) =>
    {
        var payload = await ApiEndpointSupport.ReadJsonAsync<CreateCollectionRequest>(context.Request, null, cancellationToken);
        if (payload.Error is not null)
        {
            return payload.Error;
        }

        try
        {
            var collection = await handler.HandleAsync(ApiEndpointSupport.GetClientId(context.User), payload.Value!.Name ?? string.Empty, cancellationToken);
            return Results.Created($"/api/v1/collections/{collection.Id}", collection);
        }
        catch (ArgumentException)
        {
            return ApiEndpointSupport.InvalidInput();
        }
    })
    .WithName("create_collection")
    .DeclaresBody<CreateCollectionRequest>("application/json", openApiGeneration)
    .Produces<CollectionRepresentation>(StatusCodes.Status201Created)
    .DeclaresProblem(
        StatusCodes.Status400BadRequest,
        "Bad request. The JSON body is malformed or empty, or the collection name is invalid; the handler answers " +
        "application/problem+json titled \"Invalid input\".")
    .DeclaresProblem(
        StatusCodes.Status415UnsupportedMediaType,
        "Unsupported content type. The handler only accepts application/json and answers application/problem+json " +
        "titled \"Unsupported content type\".");

app.MapPost("/api/v1/collections/{collectionId:guid}/ingestions:txt", async (
    Guid collectionId,
    HttpContext context,
    AcceptTxtIngestionHandler handler,
    CancellationToken cancellationToken) =>
    {
        var payload = await ApiEndpointSupport.ReadJsonAsync<TxtIngestionRequest>(context.Request, ApiEndpointSupport.MaxIngestionBodyBytes, cancellationToken);
        if (payload.Error is not null)
        {
            return payload.Error;
        }

        try
        {
            var result = await handler.HandleAsync(
                new AcceptTxtIngestionCommand(
                    ApiEndpointSupport.GetClientId(context.User),
                    collectionId,
                    payload.Value!.FileName ?? string.Empty,
                    Encoding.UTF8.GetBytes(payload.Value.Content ?? string.Empty),
                    payload.Value.ExternalReference),
                cancellationToken);
            return Results.Json(
                new TxtIngestionResponse(result.DocumentId, result.DocumentVersionId, result.OperationId),
                statusCode: result.IsDuplicate ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }
        catch (ResourceNotFoundException)
        {
            return ApiEndpointSupport.NotFound();
        }
        catch (ArgumentException)
        {
            return ApiEndpointSupport.InvalidInput();
        }
    })
    .WithName("accept_txt_ingestion")
    .DeclaresBody<TxtIngestionRequest>("application/json", openApiGeneration)
    .Produces<TxtIngestionResponse>(StatusCodes.Status202Accepted)
    .Produces<TxtIngestionResponse>(StatusCodes.Status200OK)
    .DeclaresProblem(
        StatusCodes.Status400BadRequest,
        "Bad request. The JSON body is malformed or empty, or the ingestion input is invalid; the handler answers " +
        "application/problem+json titled \"Invalid input\".")
    .DeclaresProblem(
        StatusCodes.Status404NotFound,
        "Not found. The collection does not exist or does not belong to the caller; the handler answers " +
        "application/problem+json titled \"Not found\".")
    .DeclaresProblem(
        StatusCodes.Status413PayloadTooLarge,
        "Request body too large. The body exceeds 1,048,576 bytes, whether declared through Content-Length or " +
        "observed while streaming; the handler answers application/problem+json titled \"Request body too large\".")
    .DeclaresProblem(
        StatusCodes.Status415UnsupportedMediaType,
        "Unsupported content type. The handler only accepts application/json and answers application/problem+json " +
        "titled \"Unsupported content type\".");

app.MapGet("/api/v1/collections/{collectionId:guid}/operations/{operationId:guid}", async (
    Guid collectionId,
    Guid operationId,
    HttpContext context,
    GetOperationStatusHandler handler,
    CancellationToken cancellationToken) =>
    {
        try
        {
            var operation = await handler.HandleAsync(ApiEndpointSupport.GetClientId(context.User), collectionId, operationId, cancellationToken);
            return Results.Ok(new OperationStatusResponse(
                operation.Id,
                operation.Status.ToString().ToLowerInvariant(),
                operation.CreatedAt,
                operation.StartedAt,
                operation.CompletedAt,
                operation.FailureStage));
        }
        catch (ResourceNotFoundException)
        {
            return ApiEndpointSupport.NotFound();
        }
    })
    .WithName("get_operation_status")
    .Produces<OperationStatusResponse>(StatusCodes.Status200OK)
    .DeclaresProblem(
        StatusCodes.Status404NotFound,
        "Not found. The operation does not exist or does not belong to the caller; the handler answers " +
        "application/problem+json titled \"Not found\".");

app.MapPost("/api/v1/retrieval:search", async (HttpContext context, SemanticRetrievalHandler handler, CancellationToken cancellationToken) =>
    {
        var payload = await ApiEndpointSupport.ReadJsonAsync<RetrievalSearchRequest>(context.Request, null, cancellationToken);
        if (payload.Error is not null)
        {
            return payload.Error;
        }

        try
        {
            var results = await handler.HandleAsync(
                new SemanticRetrievalQuery(
                    ApiEndpointSupport.GetClientId(context.User),
                    payload.Value!.CollectionIds ?? [],
                    payload.Value.Query ?? string.Empty,
                    payload.Value.TopK),
                cancellationToken);
            return Results.Ok(results);
        }
        catch (ResourceNotFoundException)
        {
            return ApiEndpointSupport.NotFound();
        }
        catch (IncompatibleEmbeddingProfilesException)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Incompatible embedding profiles");
        }
        catch (ArgumentException)
        {
            return ApiEndpointSupport.InvalidInput();
        }
    })
    .WithName("retrieval_search")
    .DeclaresBody<RetrievalSearchRequest>("application/json", openApiGeneration)
    .Produces<IReadOnlyList<SemanticRetrievalMatch>>(StatusCodes.Status200OK)
    .DeclaresProblem(
        StatusCodes.Status400BadRequest,
        "Bad request. The JSON body is malformed or empty, or the query is invalid; the handler answers " +
        "application/problem+json titled \"Invalid input\".")
    .DeclaresProblem(
        StatusCodes.Status404NotFound,
        "Not found. One of the requested collections does not exist or does not belong to the caller; the handler " +
        "answers application/problem+json titled \"Not found\".")
    .DeclaresProblem(
        StatusCodes.Status415UnsupportedMediaType,
        "Unsupported content type. The handler only accepts application/json and answers application/problem+json " +
        "titled \"Unsupported content type\".")
    .DeclaresProblem(
        StatusCodes.Status422UnprocessableEntity,
        "Unprocessable entity. The requested collections use incompatible embedding profiles; the handler answers " +
        "application/problem+json titled \"Incompatible embedding profiles\".");

if (builder.Configuration.GetValue<bool>("AdminPlane:Enabled"))
{
    app.MapAdminEndpoints();
}

app.MapHistoricalEndpoints();

app.Run();

static bool HasHistoricalScope(ClaimsPrincipal user, string requiredScope)
{
    var scopes = user.FindAll("scope")
.SelectMany(claim => (claim.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return scopes.Contains(requiredScope, StringComparer.Ordinal);
}

public partial class Program;

public sealed record TokenExchangeRequest(string? KeyId, string? Secret, string? Scope);

public sealed record CreateCollectionRequest(string? Name);

public sealed record TxtIngestionRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("file_name")] string? FileName,
    string? Content,
    [property: System.Text.Json.Serialization.JsonPropertyName("external_reference")] string? ExternalReference);

public sealed record RetrievalSearchRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("collection_ids")] IReadOnlyList<Guid>? CollectionIds,
    string? Query,
    [property: System.Text.Json.Serialization.JsonPropertyName("top_k")] int TopK);

public static class ApiEndpointSupport
{
    public const int MaxIngestionBodyBytes = 1_048_576;

    // Build-time document generation ("GetDocument.Insider") starts this host; this environment keeps the
    // generation pass free of PostgreSQL and of production secrets. See src/Rag.Api/Rag.Api.csproj.
    public const string OpenApiGenerationEnvironment = "OpenApiGeneration";

    public static IResult InvalidInput() => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid input");

    public static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static Guid GetClientId(ClaimsPrincipal user) => Guid.Parse(user.FindFirst("client_id")!.Value);

    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpRequest request, int? maxBodyBytes, CancellationToken cancellationToken)
    {
        if (!IsUtf8Json(request.ContentType))
        {
            return (default, Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported content type"));
        }

        if (maxBodyBytes is not null && request.ContentLength > maxBodyBytes)
        {
            return (default, Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request body too large"));
        }

        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[16_384];
            int read;
            while ((read = await request.Body.ReadAsync(bytes, cancellationToken)) > 0)
            {
                if (maxBodyBytes is not null && buffer.Length + read > maxBodyBytes)
                {
                    return (default, Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request body too large"));
                }

                await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
            }

            var json = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return value is null ? (default, InvalidInput()) : (value, null);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return (default, InvalidInput());
        }
    }

    private static bool IsUtf8Json(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var parts = contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(parts[0], "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Skip(1).All(part => !part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part[8..].Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));
    }
}
