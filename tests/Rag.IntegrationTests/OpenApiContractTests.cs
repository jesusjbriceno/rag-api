using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Rag.IntegrationTests;

/// <summary>
/// Generates the real v1 document in-process. The OpenApiGeneration environment skips the startup validators
/// and the background workers that would open a PostgreSQL connection, so no database is required.
/// </summary>
public sealed class OpenApiGenerationFactory : IDisposable
{
    private readonly WebApplicationFactory<global::Program> _factory;

    public OpenApiGenerationFactory()
    {
        _factory = new WebApplicationFactory<global::Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("OpenApiGeneration"));
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = provider.GetOpenApiDocumentAsync(CancellationToken.None).GetAwaiter().GetResult();
        Document = Parse(document);
    }

    public JsonNode Document { get; }

    public void Dispose() => _factory.Dispose();

    private static JsonNode Parse(OpenApiDocument document)
    {
        using var textWriter = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new OpenApiJsonWriter(textWriter);
        document.SerializeAsV31(writer);
        writer.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        return JsonNode.Parse(textWriter.ToString())!;
    }
}

public sealed class OpenApiContractTests(OpenApiGenerationFactory generation) : IClassFixture<OpenApiGenerationFactory>
{
    private const string BearerScheme = "bearerAuth";

    private static readonly string[] AdminHeaderSchemes =
    [
        "X-Admin-App-Id",
        "X-Admin-Key-Id",
        "X-Admin-Timestamp",
        "X-Admin-Signature",
        "X-Admin-Assertion",
        "Idempotency-Key",
    ];

    private static readonly string[] AnonymousPaths = ["/api/v1/health", "/api/v1/auth/token"];

    private JsonNode Document => generation.Document;

    [Fact]
    public void Document_states_the_executing_version_and_the_plane_contract()
    {
        var expectedVersion = typeof(global::Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        var description = Document["info"]?["description"]?.GetValue<string>() ?? string.Empty;

        Assert.Equal(expectedVersion, Document["info"]?["version"]?.GetValue<string>());
        Assert.Contains("off by default", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HistoricalIngestion:Enabled=false", description, StringComparison.Ordinal);
        Assert.Contains("scope", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_referenced_security_scheme_is_declared()
    {
        var schemes = Document["components"]?["securitySchemes"]?.AsObject();
        Assert.NotNull(schemes);

        foreach (var expected in AdminHeaderSchemes.Append(BearerScheme))
        {
            Assert.True(schemes!.ContainsKey(expected), $"securitySchemes is missing '{expected}'.");
        }

        foreach (var referenced in Operations(Document).SelectMany(entry => SecuritySchemeNames(entry.Operation)).Distinct(StringComparer.Ordinal))
        {
            Assert.True(schemes!.ContainsKey(referenced), $"An operation references the undeclared scheme '{referenced}'.");
        }
    }

    [Fact]
    public void Anonymous_routes_declare_no_security_at_all()
    {
        foreach (var path in AnonymousPaths)
        {
            foreach (var operation in OperationsForPath(Document, path))
            {
                Assert.Null(operation["security"]);
            }
        }
    }

    [Fact]
    public void Admin_operations_require_all_six_headers_and_not_the_bearer_scheme()
    {
        var admin = Operations(Document).Where(entry => entry.Path.StartsWith("/api/v1/admin/", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(admin);
        foreach (var (_, _, operation) in admin)
        {
            var requirement = Assert.Single(SecurityRequirements(operation));
            Assert.Equal(AdminHeaderSchemes.Length, requirement.Count);
            foreach (var header in AdminHeaderSchemes)
            {
                Assert.Contains(header, requirement);
            }

            Assert.DoesNotContain(BearerScheme, requirement);
        }
    }

    [Theory]
    [InlineData("/api/v1/historical/collections/{collectionId}/uploads", "historical:uploads.write")]
    [InlineData("/api/v1/historical/uploads/{uploadId}", "historical:uploads.write")]
    [InlineData("/api/v1/historical/uploads/{uploadId}/content", "historical:uploads.write")]
    [InlineData("/api/v1/historical/uploads/{uploadId}:commit", "historical:uploads.write")]
    [InlineData("/api/v1/historical/collections/{collectionId}/operations/{operationId}", "historical:operations.read")]
    public void Historical_operations_require_bearer_and_state_their_exact_scope(string path, string scope)
    {
        var operation = Operation(Document, path);
        Assert.NotNull(operation);
        Assert.Equal([[BearerScheme]], SecurityRequirements(operation));
        Assert.Contains(scope, operation!["description"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Remaining_public_operations_require_a_bearer_jwt()
    {
        var publicOperations = Operations(Document)
            .Where(entry => !entry.Path.StartsWith("/api/v1/admin/", StringComparison.Ordinal))
            .Where(entry => !entry.Path.StartsWith("/api/v1/historical/", StringComparison.Ordinal))
            .Where(entry => !AnonymousPaths.Contains(entry.Path, StringComparer.Ordinal))
            .ToArray();

        Assert.NotEmpty(publicOperations);
        foreach (var (_, _, operation) in publicOperations)
        {
            Assert.Equal([[BearerScheme]], SecurityRequirements(operation));
            Assert.Contains("scope claim", operation["description"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Authenticated_operations_declare_the_401_and_403_problem_responses()
    {
        var authenticated = Operations(Document)
            .Where(entry => SecuritySchemeNames(entry.Operation).Count > 0)
            .ToArray();

        Assert.NotEmpty(authenticated);
        foreach (var (path, method, operation) in authenticated)
        {
            var responses = operation["responses"]?.AsObject();
            Assert.True(responses?.ContainsKey("401") == true, $"{method.ToUpperInvariant()} {path} must declare a 401 response.");
            Assert.True(responses?.ContainsKey("403") == true, $"{method.ToUpperInvariant()} {path} must declare a 403 response.");
        }
    }

    [Fact]
    public void Health_probes_are_named_in_the_contract_note_and_not_documented_as_paths()
    {
        var description = Document["info"]?["description"]?.GetValue<string>() ?? string.Empty;
        var paths = Document["paths"]?.AsObject() ?? [];

        foreach (var path in new[] { "/api/v1/health/live", "/api/v1/health/ready" })
        {
            Assert.Contains(path, description, StringComparison.Ordinal);
            Assert.False(paths.ContainsKey(path), $"{path} is described in the contract note, not published as a path.");
        }

        Assert.Contains("plain text", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anonymous", description, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IReadOnlyList<string>> SecurityRequirements(JsonNode operation) =>
        operation["security"]?.AsArray()
            .Select(requirement => (IReadOnlyList<string>)requirement!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal).ToArray())
            .ToArray() ?? [];

    private static IReadOnlyList<string> SecuritySchemeNames(JsonNode operation) =>
        SecurityRequirements(operation).SelectMany(requirement => requirement).ToArray();

    private static JsonNode? Operation(JsonNode document, string path)
    {
        var pathItem = document["paths"]?[path]?.AsObject();
        Assert.NotNull(pathItem);
        var operation = pathItem!.FirstOrDefault(entry => entry.Key is "get" or "post" or "put" or "delete" or "patch");
        Assert.NotEqual(default, operation);
        return operation.Value;
    }

    private static IReadOnlyList<JsonNode> OperationsForPath(JsonNode document, string path)
    {
        var operation = Operation(document, path);
        return operation is null ? [] : [operation];
    }

    private static IEnumerable<(string Path, string Method, JsonNode Operation)> Operations(JsonNode document)
    {
        foreach (var path in document["paths"]?.AsObject() ?? [])
        {
            foreach (var method in path.Value?.AsObject() ?? [])
            {
                if (method.Key is "get" or "post" or "put" or "delete" or "patch")
                {
                    yield return (path.Key, method.Key, method.Value!);
                }
            }
        }
    }
}
