using System.Globalization;
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

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapPost("/api/v1/auth/token", async (TokenExchangeRequest request, CredentialExchangeHandler handler, CancellationToken cancellationToken) =>
    {
        var token = await handler.ExchangeAsync(request.KeyId, request.Secret, cancellationToken);
        return token is null
            ? Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized)
            : Results.Ok(new { access_token = token.Value, token_type = "Bearer", expires_in = 900 });
    })
    .AllowAnonymous()
    .RequireRateLimiting("credential-exchange");

app.Run();

public partial class Program;

public sealed record TokenExchangeRequest(string? KeyId, string? Secret);
