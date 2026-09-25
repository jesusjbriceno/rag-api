using System.Reflection;
using System.Text.RegularExpressions;
using Rag.HistoricalLoader.Core.Extraction;

namespace Rag.HistoricalLoader.UnitTests.Extraction;

/// <summary>
/// Unit 5 contract tests: <see cref="IExtractor"/> is the only extraction abstraction the engine
/// depends on, is swappable at composition time, and neither Core nor the engine references
/// <c>Rag.Companion</c> as a project dependency. Also guards the persisted Companion disposition
/// record (adapt) with its cited evidence IDs and limitations.
/// </summary>
public sealed class IExtractorContractTests
{
    // --- Contract shape ---------------------------------------------------

    [Fact]
    public void IExtractor_IsAnInterface()
        => Assert.True(typeof(IExtractor).IsInterface);

    [Fact]
    public void IExtractor_ExposesSingleExtractAsyncMethod()
    {
        var methods = typeof(IExtractor).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var extract = Assert.Single(methods, m => m.Name == "ExtractAsync");
        Assert.Equal(typeof(Task<ExtractionResult>), extract.ReturnType);

        var parameters = extract.GetParameters();
        Assert.Equal(2, parameters.Length);

        var request = Assert.Single(parameters, p => p.Name == "request");
        Assert.Equal(typeof(ExtractionRequest), request.ParameterType);

        var cancellationToken = Assert.Single(parameters, p => p.Name == "cancellationToken");
        Assert.Equal(typeof(CancellationToken), cancellationToken.ParameterType);
        Assert.True(cancellationToken.HasDefaultValue);
    }

    // --- Companion-free abstraction (assembly-level checks) ---------------

    [Fact]
    public void CoreAssembly_DoesNotReferenceCompanion()
    {
        var referenced = typeof(IExtractor).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, a => IsCompanionName(a.Name));
    }

    [Fact]
    public void ExtractionPublicSurface_IsCompanionFree()
    {
        var types = typeof(IExtractor).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(IExtractor).Namespace)
            .ToList();

        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            Assert.False(ReferencesCompanion(type), $"{type.FullName} references a Companion type");

            var members = type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var member in members)
            {
                foreach (var memberType in MemberTypes(member))
                {
                    Assert.False(ReferencesCompanion(memberType), $"{type.FullName}.{member.Name} references Companion via {memberType}");
                }
            }
        }
    }

    // --- FakeTextExtractor behavior --------------------------------------

    [Fact]
    public void FakeTextExtractor_ImplementsIExtractor()
        => Assert.True(typeof(IExtractor).IsAssignableFrom(typeof(FakeTextExtractor)));

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("md")]
    [InlineData("txt")]
    [InlineData(".PDF")]
    public async Task FakeTextExtractor_CompletesSupportedFormats(string format)
    {
        var result = await new FakeTextExtractor()
            .ExtractAsync(new ExtractionRequest("/corpus/sample", format), CancellationToken.None);

        Assert.Equal(ExtractionOutcome.Completed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.NormalizedText));
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task FakeTextExtractor_SkipsLegacyDocWithLibreOfficeCode()
    {
        var result = await new FakeTextExtractor()
            .ExtractAsync(new ExtractionRequest("/corpus/sample.doc", "doc"), CancellationToken.None);

        Assert.Equal(ExtractionOutcome.SkippedDocument, result.Outcome);
        Assert.Null(result.NormalizedText);
        Assert.Equal(ExtractionErrorCodes.DocLibreOfficeRequired, result.ErrorCode);
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("zip")]
    [InlineData(null)]
    public async Task FakeTextExtractor_SkipsUnsupportedFormat(string? format)
    {
        var result = await new FakeTextExtractor()
            .ExtractAsync(new ExtractionRequest("/corpus/sample", format!), CancellationToken.None);

        Assert.Equal(ExtractionOutcome.SkippedDocument, result.Outcome);
        Assert.Null(result.NormalizedText);
        Assert.Equal(ExtractionErrorCodes.ExtractorUnavailable, result.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FakeTextExtractor_ErrorsOnMissingSourcePath(string? sourcePath)
    {
        var result = await new FakeTextExtractor()
            .ExtractAsync(new ExtractionRequest(sourcePath!, "pdf"), CancellationToken.None);

        Assert.Equal(ExtractionOutcome.Error, result.Outcome);
        Assert.Null(result.NormalizedText);
        Assert.Equal(ExtractionErrorCodes.SourcePathUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task FakeTextExtractor_DoesNotEmbedSourcePathInText()
    {
        const string sourcePath = "/corpus/confidential-report.pdf";

        var result = await new FakeTextExtractor()
            .ExtractAsync(new ExtractionRequest(sourcePath, "pdf"), CancellationToken.None);

        Assert.DoesNotContain(sourcePath, result.NormalizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeTextExtractor_IsDeterministicPerSource()
    {
        var extractor = new FakeTextExtractor();

        var first = await extractor.ExtractAsync(new ExtractionRequest("/corpus/a.pdf", "pdf"), CancellationToken.None);
        var again = await extractor.ExtractAsync(new ExtractionRequest("/corpus/a.pdf", "pdf"), CancellationToken.None);
        var other = await extractor.ExtractAsync(new ExtractionRequest("/corpus/b.pdf", "pdf"), CancellationToken.None);

        Assert.Equal(first.NormalizedText, again.NormalizedText);
        Assert.NotEqual(first.NormalizedText, other.NormalizedText);
    }

    [Fact]
    public async Task FakeTextExtractor_HonorsCancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new FakeTextExtractor().ExtractAsync(
                new ExtractionRequest("/corpus/a.pdf", "pdf"),
                new CancellationToken(canceled: true)));
    }

    // --- Composition-time swappability -----------------------------------

    [Fact]
    public async Task Extractor_IsSwappableAtCompositionTime()
    {
        var composition = new ExtractionComposition(new FakeTextExtractor());

        var fromFake = await composition.ExtractAsync(new ExtractionRequest("/corpus/a.pdf", "pdf"), CancellationToken.None);

        composition.Extractor = new ReverseIdentityExtractor();
        var fromReverse = await composition.ExtractAsync(new ExtractionRequest("/corpus/a.pdf", "pdf"), CancellationToken.None);

        Assert.NotEqual(fromFake.NormalizedText, fromReverse.NormalizedText);
        Assert.Equal(ExtractionOutcome.Completed, fromReverse.Outcome);
    }

    // --- Engine / disposition records (repo-root resolved) ---------------

    [Fact]
    public void EngineProject_DoesNotProjectReferenceCompanion()
    {
        var root = FindRepoRoot();
        var csprojPath = Path.Combine(root, "src", "Rag.HistoricalLoader.Engine", "Rag.HistoricalLoader.Engine.csproj");
        Assert.True(File.Exists(csprojPath), $"Missing engine project at {csprojPath}");

        var csproj = File.ReadAllText(csprojPath);

        Assert.DoesNotContain("Rag.Companion", csproj, StringComparison.OrdinalIgnoreCase);

        // The engine may depend on exactly two in-repo projects: Core (loader types) and
        // Contracts (shared contracts). Any other project reference is a contract violation.
        Assert.Equal(AllowedEngineProjectReferences, ParseProjectReferenceNames(csproj));
    }

    [Theory]
    [InlineData("<ProjectReference Include=\"../Rag.Companion/Rag.Companion.csproj\" />")]
    [InlineData("<ProjectReference Include=\"../Rag.HistoricalLoader.Core/Rag.HistoricalLoader.Core.csproj\" />" +
                "<ProjectReference Include=\"../Rag.HistoricalLoader.Contracts/Rag.HistoricalLoader.Contracts.csproj\" />" +
                "<ProjectReference Include=\"../Rag.Companion/Rag.Companion.csproj\" />")]
    [InlineData("<ProjectReference Include=\"../Rag.HistoricalLoader.Core/Rag.HistoricalLoader.Core.csproj\" />" +
                "<ProjectReference Include=\"../Rag.Infrastructure/Rag.Infrastructure.csproj\" />")]
    public void EngineProjectReferenceAllowlist_RejectsForbiddenReferences(string csproj)
    {
        var names = ParseProjectReferenceNames(csproj);

        Assert.NotEqual(AllowedEngineProjectReferences, names);
    }

    [Fact]
    public void DispositionDocument_RecordsAdaptWithEvidenceAndLimitations()
    {
        var root = FindRepoRoot();
        var docPath = Path.Combine(root, "docs", "historical-ingestion-rebaseline", "CompanionDisposition.md");
        Assert.True(File.Exists(docPath), $"Missing disposition record at {docPath}");

        var doc = File.ReadAllText(docPath);

        // Exactly one chosen disposition: adapt.
        Assert.Contains("Outcome: adapt", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("Outcome: reuse", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("Outcome: reference", doc, StringComparison.Ordinal);

        // Cited evidence IDs.
        Assert.Contains("b11671f8-0c60-42bf-9f1b-f6f2bf27d759", doc, StringComparison.Ordinal);
        Assert.Contains("ea6a4046-6873-9b8e-5fa2-016d45779539", doc, StringComparison.Ordinal);

        // Cited limitations.
        Assert.Contains("doc_libreoffice_required", doc, StringComparison.Ordinal);
    }

    // --- Helpers ----------------------------------------------------------

    private sealed class ExtractionComposition(IExtractor extractor)
    {
        public IExtractor Extractor { get; set; } = extractor;

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
            => Extractor.ExtractAsync(request, cancellationToken);
    }

    private sealed class ReverseIdentityExtractor : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExtractionResult(
                ExtractionOutcome.Completed,
                "reverse-" + request.SourcePath,
                null));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rag.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate repository root (Rag.sln) from {AppContext.BaseDirectory}");
    }

    private static bool IsCompanionName(string? name)
        => name?.Contains("Companion", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Project references the engine is allowed to declare, ordered ordinally so the comparison is
    /// order-independent: exactly Core and Contracts.
    /// </summary>
    private static readonly string[] AllowedEngineProjectReferences =
        ["Rag.HistoricalLoader.Contracts.csproj", "Rag.HistoricalLoader.Core.csproj"];

    private static string[] ParseProjectReferenceNames(string csproj)
        => Regex.Matches(csproj, "<ProjectReference\\s+Include=\"([^\"]+)\"")
            .Select(m => Path.GetFileName(m.Groups[1].Value))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static bool ReferencesCompanion(Type? type)
    {
        if (type is null || type == typeof(void))
        {
            return false;
        }

        if (type.IsGenericParameter || type.IsByRef || type.IsPointer)
        {
            return false;
        }

        if (type.IsArray)
        {
            return ReferencesCompanion(type.GetElementType());
        }

        if (type.IsGenericType)
        {
            if (type.GetGenericArguments().Any(ReferencesCompanion))
            {
                return true;
            }

            // Check the generic type definition's own identity only; never recurse into its arguments
            // again (GetGenericTypeDefinition() on an open generic returns itself).
            var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
            return IsCompanionName(definition.Namespace) || IsCompanionName(definition.Assembly.GetName().Name);
        }

        return IsCompanionName(type.Namespace) || IsCompanionName(type.Assembly.GetName().Name);
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        _ => [],
    };
}
