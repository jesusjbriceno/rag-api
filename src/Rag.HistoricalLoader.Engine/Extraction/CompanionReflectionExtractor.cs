using System.Reflection;
using System.Runtime.Loader;
using Rag.HistoricalLoader.Core.Extraction;

namespace Rag.HistoricalLoader.Engine.Extraction;

/// <summary>
/// The selected <c>IExtractor</c> adapter (Unit 5 "adapt" disposition). It adapts the reflection-based
/// Companion extraction behind the engine's single abstraction: the Companion assembly is loaded through
/// an isolated, collectible <see cref="AssemblyLoadContext"/> and dispatched by reflection type name,
/// never through a project reference. Legacy DOC is unsupported (<c>doc_libreoffice_required</c>) and
/// unlisted formats are skipped (<c>extractor_unavailable</c>); only cancellation propagates.
/// </summary>
public sealed class CompanionReflectionExtractor : IExtractor, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> AdapterTypeNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "Rag.Companion.Adapters.PdfAdapter",
            [".docx"] = "Rag.Companion.Adapters.DocxAdapter",
            [".md"] = "Rag.Companion.Adapters.MarkdownAdapter",
        };

    private readonly string _companionDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, (object Instance, MethodInfo Extract)> _adapters =
        new(StringComparer.OrdinalIgnoreCase);
    private CompanionLoadContext? _loadContext;

    public CompanionReflectionExtractor(string companionAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companionAssemblyPath);
        _companionDirectory = Path.GetDirectoryName(Path.GetFullPath(companionAssemblyPath))
            ?? throw new ArgumentException("The companion assembly path has no directory.", nameof(companionAssemblyPath));
    }

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return Task.FromResult(new ExtractionResult(
                ExtractionOutcome.Error,
                null,
                ExtractionErrorCodes.SourcePathUnavailable));
        }

        var extension = Path.GetExtension(request.SourcePath);
        if (string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase))
        {
            return Skip(ExtractionErrorCodes.DocLibreOfficeRequired);
        }

        if (!AdapterTypeNames.TryGetValue(extension, out var typeName))
        {
            return Skip(ExtractionErrorCodes.ExtractorUnavailable);
        }

        return ExtractViaCompanionAsync(request.SourcePath, typeName, cancellationToken);
    }

    public void Dispose() => _loadContext?.Unload();

    private async Task<ExtractionResult> ExtractViaCompanionAsync(
        string sourcePath,
        string typeName,
        CancellationToken cancellationToken)
    {
        try
        {
            var (instance, extract) = GetAdapter(typeName);
            var task = (Task<string>)extract.Invoke(instance, [sourcePath, cancellationToken])!;
            var text = await task.ConfigureAwait(false);
            return new ExtractionResult(ExtractionOutcome.Completed, text, null);
        }
        catch (Exception exception)
        {
            var actual = exception is TargetInvocationException { InnerException: not null } tie
                ? tie.InnerException
                : exception;

            if (actual is OperationCanceledException)
            {
                throw actual;
            }

            return await Skip(ExtractionErrorCodes.ExtractorUnavailable).ConfigureAwait(false);
        }
    }

    private (object Instance, MethodInfo Extract) GetAdapter(string typeName)
    {
        lock (_gate)
        {
            if (_adapters.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            var context = _loadContext ??= new CompanionLoadContext(_companionDirectory);
            var assembly = context.LoadFromAssemblyPath(Path.Combine(_companionDirectory, "Rag.Companion.dll"));
            var type = assembly.GetType(typeName, throwOnError: true)!;
            var instance = Activator.CreateInstance(type)!;
            var extract = type.GetMethod("ExtractAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(typeName, "ExtractAsync");

            var adapter = (instance, extract);
            _adapters[typeName] = adapter;
            return adapter;
        }
    }

    private static Task<ExtractionResult> Skip(string errorCode)
        => Task.FromResult(new ExtractionResult(ExtractionOutcome.SkippedDocument, null, errorCode));

    private sealed class CompanionLoadContext(string directory)
        : AssemblyLoadContext($"companion-reflection-{Guid.NewGuid():N}", isCollectible: true)
    {
        private readonly string _directory = directory;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
