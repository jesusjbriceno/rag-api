using System.Reflection;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Engine.Extraction;

namespace Rag.HistoricalLoader.UnitTests.Extraction;

/// <summary>
/// Unit 10 contract tests for the selected <c>IExtractor</c> adapter. The adapter adapts the
/// reflection-based Companion extraction (Unit 5 "adapt" disposition) behind the engine abstraction,
/// never through a project reference. These tests cover the dispatch branches that do not require a
/// real Companion assembly (extension routing, path/format validation, cancellation); the live load
/// path is exercised by the local proof in a later gate.
/// </summary>
public sealed class CompanionReflectionExtractorTests
{
    [Fact]
    public void Adapter_ImplementsIExtractor()
        => Assert.True(typeof(IExtractor).IsAssignableFrom(typeof(CompanionReflectionExtractor)));

    [Fact]
    public void AdapterAssembly_DoesNotCompileReferenceCompanion()
    {
        var referenced = typeof(CompanionReflectionExtractor).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, a => a.Name?.Contains("Companion", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ExtractAsync_LegacyDoc_SkipsWithoutLoadingAssembly()
    {
        using var adapter = new CompanionReflectionExtractor("/does/not/matter/Rag.Companion.dll");

        var result = await adapter.ExtractAsync(
            new ExtractionRequest("/corpus/sample.doc", "doc"),
            CancellationToken.None);

        Assert.Equal(ExtractionOutcome.SkippedDocument, result.Outcome);
        Assert.Equal(ExtractionErrorCodes.DocLibreOfficeRequired, result.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedExtension_Skips()
    {
        using var adapter = new CompanionReflectionExtractor("/does/not/matter/Rag.Companion.dll");

        var result = await adapter.ExtractAsync(
            new ExtractionRequest("/corpus/sample.xlsx", "xlsx"),
            CancellationToken.None);

        Assert.Equal(ExtractionOutcome.SkippedDocument, result.Outcome);
        Assert.Equal(ExtractionErrorCodes.ExtractorUnavailable, result.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExtractAsync_MissingSourcePath_Errors(string? sourcePath)
    {
        using var adapter = new CompanionReflectionExtractor("/does/not/matter/Rag.Companion.dll");

        var result = await adapter.ExtractAsync(
            new ExtractionRequest(sourcePath!, "pdf"),
            CancellationToken.None);

        Assert.Equal(ExtractionOutcome.Error, result.Outcome);
        Assert.Equal(ExtractionErrorCodes.SourcePathUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsync_CancellationBeforeLoad_Propagates()
    {
        using var adapter = new CompanionReflectionExtractor("/does/not/matter/Rag.Companion.dll");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            adapter.ExtractAsync(
                new ExtractionRequest("/corpus/sample.pdf", "pdf"),
                new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Adapter_DispatchSurface_UsesReflectionTypeNames()
    {
        // The adapter must dispatch by reflection type name, never by a Companion compile-time type.
        var type = typeof(CompanionReflectionExtractor);
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        foreach (var member in members)
        {
            foreach (var memberType in MemberTypes(member))
            {
                Assert.False(
                    ReferencesCompanion(memberType),
                    $"{type.FullName}.{member.Name} compile-references Companion via {memberType}");
            }
        }
    }

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
            return type.GetGenericArguments().Any(ReferencesCompanion);
        }

        return type.Namespace?.Contains("Companion", StringComparison.OrdinalIgnoreCase) == true
            || type.Assembly.GetName().Name?.Contains("Companion", StringComparison.OrdinalIgnoreCase) == true;
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
