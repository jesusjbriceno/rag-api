using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Rag.Application;
using Rag.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
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
        .Build());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("credential-exchange", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Internal server error").ExecuteAsync(context)));
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapPost("/api/v1/auth/token", async (TokenExchangeRequest request, CredentialExchangeHandler handler, CancellationToken cancellationToken) =>
    {
        var token = await handler.ExchangeAsync(request.KeyId, request.Secret, cancellationToken);
        return token is null
            ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized")
            : Results.Ok(new { access_token = token.Value, token_type = "Bearer", expires_in = 900 });
    })
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
    });

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
                new { document_id = result.DocumentId, document_version_id = result.DocumentVersionId, operation_id = result.OperationId },
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
    });

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
            return Results.Ok(new
            {
                id = operation.Id,
                status = operation.Status.ToString().ToLowerInvariant(),
                created_at = operation.CreatedAt,
                started_at = operation.StartedAt,
                completed_at = operation.CompletedAt,
                failure_stage = operation.FailureStage,
            });
        }
        catch (ResourceNotFoundException)
        {
            return ApiEndpointSupport.NotFound();
        }
    });

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
    });

app.Run();

public partial class Program;

public sealed record TokenExchangeRequest(string? KeyId, string? Secret);

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
