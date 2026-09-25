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

    /// <summary>
    /// The one anonymous operation whose handler authenticates the credential itself and therefore answers an
    /// <c>application/problem+json</c> 401, unlike the health probes that only the anonymous middleware skips.
    /// </summary>
    private const string AnonymousTokenPath = "/api/v1/auth/token";

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
    public void Every_declared_security_scheme_is_referenced_and_every_referenced_scheme_is_declared()
    {
        var schemes = Document["components"]?["securitySchemes"]?.AsObject();
        Assert.NotNull(schemes);
        Assert.Equal(AdminHeaderSchemes.Length + 1, schemes!.Count);

        foreach (var expected in AdminHeaderSchemes.Append(BearerScheme))
        {
            Assert.True(schemes.ContainsKey(expected), $"securitySchemes is missing '{expected}'.");
        }

        var referenced = Operations(Document)
            .SelectMany(entry => SecuritySchemeNames(entry.Operation))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var declared in schemes.Select(scheme => scheme.Key))
        {
            Assert.Contains(declared, referenced);
        }

        foreach (var reference in referenced)
        {
            Assert.True(schemes.ContainsKey(reference), $"An operation references the undeclared scheme '{reference}'.");
        }
    }

    [Fact]
    public void Anonymous_routes_declare_no_security_at_all()
    {
        foreach (var path in AnonymousPaths)
        {
            var operations = OperationsForPath(Document, path);
            Assert.NotEmpty(operations);
            foreach (var operation in operations)
            {
                Assert.Null(operation["security"]);
            }
        }
    }

    [Fact]
    public void Admin_operations_require_all_six_headers_and_not_the_bearer_scheme()
    {
        var admin = Operations(Document).Where(entry => entry.Path.StartsWith("/api/v1/admin/", StringComparison.Ordinal)).ToArray();

        Assert.Equal(9, admin.Length);
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
        var operations = OperationsForPath(Document, path);
        Assert.NotEmpty(operations);
        foreach (var operation in operations)
        {
            Assert.Equal([[BearerScheme]], SecurityRequirements(operation));
            Assert.Contains(scope, operation["description"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        }
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
    public void Authentication_responses_match_the_plane_that_produces_them()
    {
        const string adminPath = "/api/v1/admin/";
        var checkedOperations = 0;

        foreach (var (path, method, operation) in Operations(Document))
        {
            var responses = Responses(operation);
            if (SecuritySchemeNames(operation).Count == 0)
            {
                Assert.False(responses.ContainsKey("403"), $"{method.ToUpperInvariant()} {path} is anonymous and must not declare a 403.");
                if (path == AnonymousTokenPath)
                {
                    // The authentication middleware never runs here; this is the handler's own 401 and it is a problem body.
                    AssertProblemJsonResponse(operation, "401", method, path);
                }
                else
                {
                    Assert.False(responses.ContainsKey("401"), $"{method.ToUpperInvariant()} {path} is anonymous and must not declare a 401.");
                }

                checkedOperations++;
                continue;
            }

            AssertProblemJsonResponse(operation, "401", method, path);
            if (path.StartsWith(adminPath, StringComparison.Ordinal))
            {
                Assert.False(
                    responses.ContainsKey("403"),
                    $"{method.ToUpperInvariant()} {path} is an admin operation and has no reachable 403.");
            }
            else
            {
                AssertProblemJsonResponse(operation, "403", method, path);
            }

            checkedOperations++;
        }

        Assert.Equal(20, checkedOperations);
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

    [Fact]
    public void The_document_publishes_twenty_operations_over_eighteen_paths_with_unique_operation_ids()
    {
        var paths = Document["paths"]?.AsObject();
        Assert.NotNull(paths);
        Assert.Equal(18, paths!.Count);

        var operations = Operations(Document).ToArray();
        Assert.Equal(20, operations.Length);

        var operationIds = operations.Select(entry => entry.Operation["operationId"]?.GetValue<string>()).ToArray();
        Assert.All(operationIds, operationId => Assert.False(string.IsNullOrWhiteSpace(operationId), "Every operation needs an operationId."));
        Assert.Equal(operationIds.Length, operationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(operationIds, operationId => Assert.Matches("^[a-z][a-z0-9_]*$", operationId!));
    }

    [Fact]
    public void Every_operation_carries_exactly_one_plane_tag_and_no_class_name_tag_survives()
    {
        string[] planeTags = ["Public", "Admin", "Historical"];
        var used = new List<string>();
        foreach (var (path, method, operation) in Operations(Document))
        {
            var tags = operation["tags"]?.AsArray().Select(tag => tag?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
            Assert.True(tags.Length == 1, $"{method.ToUpperInvariant()} {path} must carry exactly one tag.");
            Assert.Contains(tags[0], planeTags);
            used.Add(tags[0]);
        }

        Assert.Equal(planeTags.Order(StringComparer.Ordinal), used.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        var declared = Document["tags"]?.AsArray()
            .Select(tag => tag?["name"]?.GetValue<string>() ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(planeTags.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(declared, tag => tag.Contains("Endpoints", StringComparison.Ordinal) || tag.StartsWith("Rag.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/v1/health", "get", "200", "HealthResponse")]
    [InlineData("/api/v1/auth/token", "post", "200", "TokenResponse")]
    [InlineData("/api/v1/collections", "post", "201", "CollectionRepresentation")]
    [InlineData("/api/v1/collections/{collectionId}/ingestions:txt", "post", "200,202", "TxtIngestionResponse")]
    [InlineData("/api/v1/collections/{collectionId}/operations/{operationId}", "get", "200", "OperationStatusResponse")]
    [InlineData("/api/v1/retrieval:search", "post", "200", "SemanticRetrievalMatch")]
    [InlineData("/api/v1/admin/clients", "post", "200,201", "AdminClientMetadata")]
    [InlineData("/api/v1/admin/clients", "get", "200", "AdminClientPage")]
    [InlineData("/api/v1/admin/clients/{clientId}", "get", "200", "AdminClientDetail")]
    [InlineData("/api/v1/admin/clients/{clientId}/credentials", "post", "201", "AdminCredentialDelivery")]
    [InlineData("/api/v1/admin/clients/{clientId}/credentials", "get", "200", "AdminCredentialMetadata")]
    [InlineData("/api/v1/admin/credentials/{credentialId}", "get", "200", "AdminCredentialMetadata")]
    [InlineData("/api/v1/admin/credentials/{credentialId}/rotate", "post", "200", "AdminCredentialDelivery")]
    [InlineData("/api/v1/admin/credentials/{credentialId}/revoke", "post", "200", "AdminCredentialMetadata")]
    [InlineData("/api/v1/admin/audit", "get", "200", "AdminAuditPage")]
    [InlineData("/api/v1/historical/collections/{collectionId}/uploads", "post", "200,201", "ReserveHistoricalUploadResponse")]
    [InlineData("/api/v1/historical/uploads/{uploadId}/content", "put", "200", "PublishedHistoricalUploadResponse")]
    [InlineData("/api/v1/historical/uploads/{uploadId}:commit", "post", "200", "CommitHistoricalUploadResponse")]
    [InlineData("/api/v1/historical/uploads/{uploadId}", "get", "200", "HistoricalUploadStatusResponse")]
    [InlineData("/api/v1/historical/collections/{collectionId}/operations/{operationId}", "get", "200", "HistoricalOperationTelemetryResponse")]
    public void Every_operation_states_its_real_success_contract(string path, string method, string successCodes, string schemaType)
    {
        var operation = OperationFor(Document, path, method);
        Assert.NotNull(operation);
        var responses = operation!["responses"]?.AsObject();
        Assert.NotNull(responses);

        var declared = responses!.Select(response => response.Key)
            .Where(code => code.StartsWith('2'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = successCodes.Split(',').Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, declared);

        foreach (var code in expected)
        {
            Assert.True(responses.ContainsKey(code), $"{method.ToUpperInvariant()} {path} must declare {code}.");
            Assert.Contains(schemaType, SchemaNames(responses[code]?["content"]));
        }
    }

    [Theory]
    [InlineData("get", "/api/v1/health", "200,500")]
    [InlineData("post", "/api/v1/auth/token", "200,400,401,415,429,500")]
    [InlineData("post", "/api/v1/collections", "201,400,401,403,415,500")]
    [InlineData("post", "/api/v1/collections/{collectionId}/ingestions:txt", "200,202,400,401,403,404,413,415,500")]
    [InlineData("get", "/api/v1/collections/{collectionId}/operations/{operationId}", "200,401,403,404,500")]
    [InlineData("post", "/api/v1/retrieval:search", "200,400,401,403,404,415,422,500")]
    [InlineData("post", "/api/v1/admin/clients", "200,201,400,401,409,415,500")]
    [InlineData("get", "/api/v1/admin/clients", "200,400,401,500")]
    [InlineData("get", "/api/v1/admin/clients/{clientId}", "200,401,404,500")]
    [InlineData("post", "/api/v1/admin/clients/{clientId}/credentials", "201,400,401,404,409,415,500")]
    [InlineData("get", "/api/v1/admin/clients/{clientId}/credentials", "200,401,404,500")]
    [InlineData("get", "/api/v1/admin/credentials/{credentialId}", "200,401,404,500")]
    [InlineData("post", "/api/v1/admin/credentials/{credentialId}/rotate", "200,400,401,404,409,500")]
    [InlineData("post", "/api/v1/admin/credentials/{credentialId}/revoke", "200,400,401,404,409,500")]
    [InlineData("get", "/api/v1/admin/audit", "200,400,401,500")]
    [InlineData("post", "/api/v1/historical/collections/{collectionId}/uploads", "200,201,400,401,403,404,409,413,415,429,500")]
    [InlineData("put", "/api/v1/historical/uploads/{uploadId}/content", "200,400,401,403,404,409,413,415,429,500")]
    [InlineData("post", "/api/v1/historical/uploads/{uploadId}:commit", "200,401,403,404,409,500")]
    [InlineData("get", "/api/v1/historical/uploads/{uploadId}", "200,401,403,404,500")]
    [InlineData("get", "/api/v1/historical/collections/{collectionId}/operations/{operationId}", "200,401,403,404,500")]
    public void Every_operation_declares_its_exact_response_code_set(string method, string path, string expected)
    {
        var operation = OperationFor(Document, path, method);
        Assert.NotNull(operation);
        var declared = Responses(operation!).Select(response => response.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Split(',').Order(StringComparer.Ordinal).ToArray(), declared);
    }

    [Theory]
    [InlineData("post", "/api/v1/collections", "400,415")]
    [InlineData("post", "/api/v1/collections/{collectionId}/ingestions:txt", "400,415")]
    [InlineData("post", "/api/v1/retrieval:search", "400,415")]
    [InlineData("post", "/api/v1/admin/clients", "400,415")]
    [InlineData("post", "/api/v1/admin/clients/{clientId}/credentials", "400,415")]
    [InlineData("post", "/api/v1/historical/collections/{collectionId}/uploads", "400,415")]
    [InlineData("put", "/api/v1/historical/uploads/{uploadId}/content", "400,415")]
    public void Hand_parsed_endpoints_declare_problem_json_bodies_for_their_own_4xx(string method, string path, string codes)
    {
        var operation = OperationFor(Document, path, method);
        Assert.NotNull(operation);
        var responses = Responses(operation!);
        foreach (var code in codes.Split(','))
        {
            var content = responses[code]?["content"]?.AsObject();
            Assert.NotNull(content);
            Assert.True(
                content!.ContainsKey("application/problem+json"),
                $"{method.ToUpperInvariant()} {path} {code} must declare an application/problem+json body.");
        }
    }

    [Theory]
    [InlineData("post", "/api/v1/auth/token", "400")]
    [InlineData("post", "/api/v1/historical/collections/{collectionId}/uploads", "429")]
    public void Dual_shape_statuses_describe_the_problem_and_the_bodyless_branch(string method, string path, string code)
    {
        var operation = OperationFor(Document, path, method);
        Assert.NotNull(operation);

        AssertProblemJsonResponse(operation!, code, method, path);
        var description = Responses(operation!)[code]?["description"]?.GetValue<string>() ?? string.Empty;
        Assert.Contains("no response body", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_after_is_declared_on_exactly_the_operations_that_can_return_it()
    {
        var carrying = Operations(Document)
            .SelectMany(entry => Responses(entry.Operation)
                .Where(response => response.Value?["headers"]?.AsObject()?.ContainsKey("Retry-After") == true)
                .Select(response => $"{entry.Operation["operationId"]!.GetValue<string>()} {response.Key}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "create_client 409",
                "issue_credential 409",
                "publish_historical_upload_content 429",
                "reserve_historical_upload 429",
                "revoke_credential 409",
                "rotate_credential 409",
            ],
            carrying);
    }

    [Theory]
    [InlineData("/api/v1/health", "get")]
    [InlineData("/api/v1/auth/token", "post")]
    [InlineData("/api/v1/collections", "post")]
    [InlineData("/api/v1/collections/{collectionId}/ingestions:txt", "post")]
    [InlineData("/api/v1/collections/{collectionId}/operations/{operationId}", "get")]
    [InlineData("/api/v1/retrieval:search", "post")]
    [InlineData("/api/v1/admin/clients", "get,post")]
    [InlineData("/api/v1/admin/clients/{clientId}", "get")]
    [InlineData("/api/v1/admin/clients/{clientId}/credentials", "get,post")]
    [InlineData("/api/v1/admin/credentials/{credentialId}", "get")]
    [InlineData("/api/v1/admin/credentials/{credentialId}/rotate", "post")]
    [InlineData("/api/v1/admin/credentials/{credentialId}/revoke", "post")]
    [InlineData("/api/v1/admin/audit", "get")]
    [InlineData("/api/v1/historical/collections/{collectionId}/uploads", "post")]
    [InlineData("/api/v1/historical/uploads/{uploadId}", "get")]
    [InlineData("/api/v1/historical/uploads/{uploadId}/content", "put")]
    [InlineData("/api/v1/historical/uploads/{uploadId}:commit", "post")]
    [InlineData("/api/v1/historical/collections/{collectionId}/operations/{operationId}", "get")]
    public void Every_path_declares_its_exact_verb_set(string path, string verbs)
    {
        var pathItem = Document["paths"]?[path]?.AsObject();
        Assert.NotNull(pathItem);
        var declared = pathItem!.Select(entry => entry.Key)
            .Where(IsOperation)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(verbs.Split(',').Order(StringComparer.Ordinal).ToArray(), declared);
    }

    [Theory]
    [InlineData("/api/v1/admin/credentials/{credentialId}/rotate")]
    [InlineData("/api/v1/admin/credentials/{credentialId}/revoke")]
    public void IfMatch_is_a_required_quoted_version_header_on_the_two_mutation_endpoints(string path)
    {
        var operation = OperationFor(Document, path, "post");
        Assert.NotNull(operation);
        var parameters = Parameters(operation!)
            .Where(parameter => parameter["name"]?.GetValue<string>() == "If-Match")
            .ToArray();
        var parameter = Assert.Single(parameters);
        Assert.Equal("header", parameter["in"]?.GetValue<string>());
        Assert.True(parameter["required"]?.GetValue<bool>());
        Assert.Contains("\"v3\"", parameter["description"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void IfMatch_is_declared_on_exactly_the_two_mutation_endpoints()
    {
        var carrying = Operations(Document)
            .Where(entry => Parameters(entry.Operation).Any(parameter => parameter["name"]?.GetValue<string>() == "If-Match"))
            .Select(entry => entry.Operation["operationId"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["revoke_credential", "rotate_credential"], carrying);
    }

    [Theory]
    [InlineData("/api/v1/auth/token", "post", "application/json", "TokenExchangeRequest")]
    [InlineData("/api/v1/collections", "post", "application/json", "CreateCollectionRequest")]
    [InlineData("/api/v1/collections/{collectionId}/ingestions:txt", "post", "application/json", "TxtIngestionRequest")]
    [InlineData("/api/v1/retrieval:search", "post", "application/json", "RetrievalSearchRequest")]
    [InlineData("/api/v1/admin/clients", "post", "application/json", "AdminCreateClientRequest")]
    [InlineData("/api/v1/admin/clients/{clientId}/credentials", "post", "application/json", "AdminIssueCredentialRequest")]
    [InlineData("/api/v1/historical/collections/{collectionId}/uploads", "post", "application/json", "ReserveHistoricalUploadRequest")]
    [InlineData("/api/v1/historical/uploads/{uploadId}/content", "put", "text/plain", "string")]
    public void Declared_request_bodies_state_their_content_type_and_schema(string path, string method, string contentType, string schemaType)
    {
        var operation = OperationFor(Document, path, method);
        Assert.NotNull(operation);
        var content = operation!["requestBody"]?["content"]?.AsObject();
        Assert.NotNull(content);
        Assert.True(content!.ContainsKey(contentType), $"{method.ToUpperInvariant()} {path} must declare a {contentType} request body.");
        Assert.Contains(schemaType, SchemaNames(content));
    }

    private static IReadOnlyList<IReadOnlyList<string>> SecurityRequirements(JsonNode operation) =>
        operation["security"]?.AsArray()
            .Select(requirement => (IReadOnlyList<string>)requirement!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal).ToArray())
            .ToArray() ?? [];

    private static IReadOnlyList<string> SecuritySchemeNames(JsonNode operation) =>
        SecurityRequirements(operation).SelectMany(requirement => requirement).ToArray();

    private static IReadOnlyList<JsonNode> OperationsForPath(JsonNode document, string path)
    {
        var pathItem = document["paths"]?[path]?.AsObject();
        Assert.NotNull(pathItem);
        return pathItem!.Where(entry => IsOperation(entry.Key)).Select(entry => entry.Value!).ToArray();
    }

    private static JsonNode? OperationFor(JsonNode document, string path, string method)
    {
        var pathItem = document["paths"]?[path]?.AsObject();
        Assert.NotNull(pathItem);
        var operation = pathItem![method];
        Assert.NotNull(operation);
        return operation;
    }

    private static IEnumerable<(string Path, string Method, JsonNode Operation)> Operations(JsonNode document)
    {
        foreach (var path in document["paths"]?.AsObject() ?? [])
        {
            foreach (var method in path.Value?.AsObject() ?? [])
            {
                if (IsOperation(method.Key))
                {
                    yield return (path.Key, method.Key, method.Value!);
                }
            }
        }
    }

    private static readonly string[] NonOperationPathItemKeys = ["parameters", "summary", "description", "servers"];

    /// <summary>
    /// The inverted OpenAPI rule: inside a path item every key that is not a path-item field is an operation, so a
    /// HEAD, OPTIONS or TRACE verb cannot stay invisible to the assertions.
    /// </summary>
    private static bool IsOperation(string key) => !NonOperationPathItemKeys.Contains(key, StringComparer.Ordinal);

    private static JsonObject Responses(JsonNode operation) => operation["responses"]?.AsObject() ?? new JsonObject();

    private static void AssertProblemJsonResponse(JsonNode operation, string code, string method, string path)
    {
        var response = Responses(operation)[code];
        Assert.True(response is not null, $"{method.ToUpperInvariant()} {path} must declare a {code} response.");
        var content = response!["content"]?.AsObject();
        Assert.True(
            content?.ContainsKey("application/problem+json") == true,
            $"{method.ToUpperInvariant()} {path} {code} must declare an application/problem+json body.");
    }

    private static IReadOnlyList<JsonNode> Parameters(JsonNode operation) =>
        operation["parameters"]?.AsArray()?.Select(parameter => parameter!).ToArray() ?? [];

    private static IReadOnlyList<string> SchemaNames(JsonNode? content)
    {
        var names = new List<string>();
        var mediaTypes = content?.AsObject();
        if (mediaTypes is null)
        {
            return names;
        }

        foreach (var mediaType in mediaTypes)
        {
            CollectNames(mediaType.Value?["schema"], names);
        }

        return names;
    }

    private static void CollectNames(JsonNode? schema, List<string> names)
    {
        if (schema is null)
        {
            return;
        }

        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            names.Add(reference[(reference.LastIndexOf('/') + 1)..]);
        }
        else if (schema["type"]?.GetValue<string>() is { } type)
        {
            names.Add(type);
        }

        if (schema["items"] is { } items)
        {
            CollectNames(items, names);
        }
    }
}
