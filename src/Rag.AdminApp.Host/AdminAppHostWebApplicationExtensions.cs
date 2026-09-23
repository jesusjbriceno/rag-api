using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Rag.AdminApp;
using Rag.Infrastructure;

namespace Rag.AdminApp.Host;

public static class AdminAppHostWebApplicationExtensions
{
    public static WebApplication MapAdminAppHost(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok());
        app.MapGet("/health/ready", async (AdminApiHealthProbe probe, CancellationToken cancellationToken) =>
            await probe.IsReachableAsync(cancellationToken)
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        var group = app.MapGroup("/api/v1/admin");
        foreach (var operation in AdminProxyOperationCatalog.Operations)
        {
            var current = operation;
            RequestDelegate handler = async context =>
            {
                if (context.Items[AdminAppHostKeys.AuthenticatedSubject] is not string subject)
                {
                    return;
                }

                var client = context.RequestServices.GetRequiredService<AdminApiClient>();
                await client.SendAsync(current, context, subject, context.RequestAborted);
            };

            var builder = current.Method == "POST"
                ? group.MapPost(current.Template, (Delegate)handler)
                : group.MapGet(current.Template, (Delegate)handler);
            builder.AddEndpointFilter<CloudflareAdminEndpointFilter>();
        }

        return app;
    }
}

/// <summary>
/// A single closed administrative operation the BFF is permitted to proxy. The
/// proxy accepts only these descriptors, never an arbitrary destination URI.
/// </summary>
public sealed record AdminProxyOperation(string Method, string Template, bool IfMatchApplicable);

public static class AdminProxyOperationCatalog
{
    public static readonly IReadOnlyList<AdminProxyOperation> Operations =
    [
        new("POST", "/clients", false),
        new("GET", "/clients", false),
        new("GET", "/clients/{clientId:guid}", false),
        new("POST", "/clients/{clientId:guid}/credentials", false),
        new("GET", "/clients/{clientId:guid}/credentials", false),
        new("GET", "/credentials/{credentialId:guid}", false),
        new("POST", "/credentials/{credentialId:guid}/rotate", true),
        new("POST", "/credentials/{credentialId:guid}/revoke", true),
        new("GET", "/audit", false),
    ];
}

public sealed record AdminProxySignedRequest(AdminMachineProof Proof, string Signature, string AssertionJws);

/// <summary>
/// Builds the verifier-compatible downstream identity for one authorised request:
/// a 60-second RS256 assertion plus an HMAC-SHA256 machine proof signed with the
/// configured current secret. PreviousSecret is never selected for signing.
/// </summary>
public sealed class AdminProxyRequestSigner(
    AdminAssertionIssuer issuer,
    AdminAssertionIssuerOptions assertionOptions,
    AdminAppAuthOptions appAuthOptions)
{
    public AdminProxySignedRequest Sign(
        string method,
        string pathAndQuery,
        byte[] body,
        string subject,
        string idempotencyKey,
        DateTimeOffset now)
    {
        var assertionJws = issuer.Issue(subject, now);
        var app = appAuthOptions.Apps.First(candidate =>
            string.Equals(candidate.AppId, assertionOptions.AppId, StringComparison.Ordinal));

        var proof = new AdminMachineProof(
            method,
            pathAndQuery,
            ComputeSha256Hex(body),
            ComputeSha256Hex(Encoding.UTF8.GetBytes(assertionJws)),
            app.AppId!,
            app.KeyId!,
            now.ToUnixTimeSeconds(),
            idempotencyKey);

        var signature = Sign(AdminMachineProofVerifier.Canonicalize(proof), app.CurrentSecret!);
        return new AdminProxySignedRequest(proof, signature, assertionJws);
    }

    private static string Sign(string canonical, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// Forwards a single closed operation downstream. It never clones the inbound
/// header set: it copies only the small semantic allowlist and then writes the
/// generated authentication headers last.
/// </summary>
public sealed class AdminApiClient(HttpClient httpClient, AdminProxyRequestSigner signer)
{
    private static readonly string[] ResponseHeaderAllowlist = ["Content-Type", "Location", "ETag", "Retry-After"];

    public async Task SendAsync(
        AdminProxyOperation operation,
        HttpContext context,
        string subject,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        if (body is null)
        {
            await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "Request body too large");
            return;
        }

        var idempotencyKey = ResolveIdempotencyKey(context.Request);
        if (idempotencyKey is null)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Invalid input");
            return;
        }

        var pathAndQuery = context.Request.Path.ToString() + context.Request.QueryString.ToString();
        var request = new HttpRequestMessage(new HttpMethod(operation.Method), pathAndQuery);

        CopyInboundHeaders(context.Request, request, operation, idempotencyKey);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            CopyContentType(context.Request, request.Content);
        }

        var signed = signer.Sign(operation.Method, pathAndQuery, body, subject, idempotencyKey, DateTimeOffset.UtcNow);
        ApplySignedHeaders(request, signed);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway, "Bad gateway");
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context.Response);
            await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > AdminAuthenticationContract.MaxBodyBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static string? ResolveIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(AdminAuthenticationContract.IdempotencyKeyHeader, out var values))
        {
            return Guid.NewGuid().ToString("N");
        }

        return values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;
    }

    private static void CopyInboundHeaders(
        HttpRequest inbound,
        HttpRequestMessage outbound,
        AdminProxyOperation operation,
        string idempotencyKey)
    {
        if (inbound.Headers.TryGetValue("Accept", out var accept) && accept.Count == 1 && !string.IsNullOrWhiteSpace(accept[0]))
        {
            outbound.Headers.TryAddWithoutValidation("Accept", accept[0]);
        }

        outbound.Headers.TryAddWithoutValidation(AdminAuthenticationContract.IdempotencyKeyHeader, idempotencyKey);

        if (operation.IfMatchApplicable &&
            inbound.Headers.TryGetValue("If-Match", out var ifMatch) &&
            ifMatch.Count == 1 &&
            !string.IsNullOrWhiteSpace(ifMatch[0]))
        {
            outbound.Headers.TryAddWithoutValidation("If-Match", ifMatch[0]);
        }
    }

    private static void CopyContentType(HttpRequest inbound, HttpContent content)
    {
        if (inbound.Headers.TryGetValue("Content-Type", out var contentType) && contentType.Count == 1)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType[0]);
        }
    }

    private static void ApplySignedHeaders(HttpRequestMessage request, AdminProxySignedRequest signed)
    {
        request.Headers.TryAddWithoutValidation(AdminAuthenticationContract.AppIdHeader, signed.Proof.AppId);
        request.Headers.TryAddWithoutValidation(AdminAuthenticationContract.KeyIdHeader, signed.Proof.KeyId);
        request.Headers.TryAddWithoutValidation(
            AdminAuthenticationContract.TimestampHeader,
            signed.Proof.TimestampUnixSeconds.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(AdminAuthenticationContract.SignatureHeader, signed.Signature);
        request.Headers.TryAddWithoutValidation(AdminAuthenticationContract.AssertionHeader, signed.AssertionJws);
    }

    private static void CopyResponseHeaders(HttpResponseMessage response, HttpResponse target)
    {
        foreach (var name in ResponseHeaderAllowlist)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                target.Headers[name] = values.ToArray();
            }

            if (response.Content.Headers.TryGetValues(name, out var contentValues))
            {
                target.Headers[name] = contentValues.ToArray();
            }
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync($"{{\"status\":{statusCode},\"title\":\"{title}\"}}");
    }
}

/// <summary>
/// Authenticates and authorises every administrative BFF endpoint before any
/// assertion or machine proof is issued and before the API is contacted.
/// </summary>
public sealed class CloudflareAdminEndpointFilter(
    CloudflareAssertionHeaderReader headerReader,
    CloudflareAccessValidator validator,
    AdminSubjectAllowlist allowlist) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var token = headerReader.Read(context.HttpContext.Request);
        var subject = token is null ? null : validator.Validate(token);
        if (subject is null)
        {
            return ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized"));
        }

        if (!allowlist.Contains(subject))
        {
            return ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden"));
        }

        context.HttpContext.Items[AdminAppHostKeys.AuthenticatedSubject] = subject;
        return next(context);
    }
}

/// <summary>
/// Anonymous liveness probe for the configured internal API origin. Readiness only
/// proves the BFF can reach its immediate dependency; it never forwards credentials.
/// </summary>
public sealed class AdminApiHealthProbe(HttpClient httpClient)
{
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("api/v1/health/live", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}

internal static class AdminAppHostKeys
{
    public const string AuthenticatedSubject = "adminapp.authenticated_subject";
}
