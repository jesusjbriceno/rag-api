using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Rag.Api;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

public sealed class AdminObservabilityTests
{
    private const string Secret = "super-secret-value";
    private const string Assertion = "eyJhbGciOiJSUzI1NiJ9.payload.signature";
    private const string Header = "Bearer forged-token";
    private const string Body = "{\"name\":\"secret-body\"}";
    private const string Hash = "sha256:hash-secret";
    private const string Salt = "salt-secret";
    private const string Query = "?cursor=query-secret&limit=50";

    [Fact]
    public async Task Successful_admin_request_logs_only_safe_fields()
    {
        var capture = new CapturingLogger();
        var middleware = Build(capture, context =>
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.User = BuildPrincipal("admin-app", "cf-subject-123");
            return Task.CompletedTask;
        });

        var context = BuildContext("/api/v1/admin/clients", "POST");

        await middleware.InvokeAsync(context);

        var message = Assert.Single(capture.Messages);
        Assert.Contains("201", message);
        Assert.Contains("/api/v1/admin/clients", message);
        Assert.Contains("admin-app", message);
        Assert.Contains("cf-subject-123", message);
        AssertDoesNotLeak(message);
    }

    [Fact]
    public async Task Failed_auth_logs_reason_category_without_secret_material()
    {
        var capture = new CapturingLogger();
        var middleware = Build(capture, _ => Task.CompletedTask);

        var context = BuildContext("/api/v1/admin/clients", "POST");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Items[AdminObservabilityKeys.AuthFailureReason] = AdminAuthFailureReason.ReplayRejected;
        context.Items[AdminObservabilityKeys.ProblemCode] = "unauthorized";

        await middleware.InvokeAsync(context);

        var message = Assert.Single(capture.Messages);
        Assert.Contains("401", message);
        Assert.Contains("replay_rejected", message);
        Assert.Contains("unauthorized", message);
        AssertDoesNotLeak(message);
    }

    [Fact]
    public async Task Conflict_response_logs_the_problem_code_without_the_body()
    {
        var capture = new CapturingLogger();
        var middleware = Build(capture, context =>
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.User = BuildPrincipal("admin-app", "cf-subject-123");
            return Task.CompletedTask;
        });

        var context = BuildContext("/api/v1/admin/clients", "POST");
        context.Items[AdminObservabilityKeys.ProblemCode] = "duplicate";

        await middleware.InvokeAsync(context);

        var message = Assert.Single(capture.Messages);
        Assert.Contains("duplicate", message);
        AssertDoesNotLeak(message);
    }

    [Fact]
    public async Task Non_admin_paths_are_not_observed()
    {
        var capture = new CapturingLogger();
        var middleware = Build(capture, _ => Task.CompletedTask);

        var context = BuildContext("/api/v1/collections", "POST");

        await middleware.InvokeAsync(context);

        Assert.Empty(capture.Messages);
    }

    private static void AssertDoesNotLeak(string message)
    {
        Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assertion, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Header, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Hash, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Salt, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Query, message, StringComparison.Ordinal);
    }

    private static AdminObservabilityMiddleware Build(CapturingLogger capture, RequestDelegate next) =>
        new(next, new LoggerFactory([new CapturingLoggerProvider(capture)]).CreateLogger<AdminObservabilityMiddleware>());

    private static DefaultHttpContext BuildContext(string path, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(Query);
        context.Request.Headers["X-Untrusted-Secret"] = Header;
        context.Request.Headers[AdminAuthenticationDefaults.AssertionHeader] = Assertion;
        context.Request.Headers["X-Untrusted-Hash"] = Hash;
        context.Request.Headers["X-Untrusted-Salt"] = Salt;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body));
        // Seed the context with secret-bearing artifacts that the middleware must NOT read.
        context.Items["secret"] = Secret;
        context.Items["assertion"] = Assertion;
        context.Items["header"] = Header;
        context.Items["body"] = Body;
        context.Items["hash"] = Hash;
        context.Items["salt"] = Salt;
        return context;
    }

    private static ClaimsPrincipal BuildPrincipal(string appId, string actorSubject) =>
        new(new ClaimsIdentity(
            [
                new Claim(AdminAuthenticationDefaults.AppIdClaim, appId),
                new Claim(AdminAuthenticationDefaults.ActorSubjectClaim, actorSubject),
            ],
            "Admin"));

    private sealed class CapturingLoggerProvider(CapturingLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
