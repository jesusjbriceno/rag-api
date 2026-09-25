using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Rag.HistoricalLoader.Core.Benchmark;

namespace Rag.HistoricalLoader.Engine;

/// <summary>
/// Loads the existing <c>Rag.Companion</c> assembly through a reflection boundary (never a project
/// reference) and dispatches PDF/Markdown/DOCX extraction to the companion's parameterless adapters.
/// Content-safe: only the normalized UTF-8 byte count is returned, never the text, and nothing is
/// persisted or logged. Legacy DOC is intentionally unsupported; users should convert DOC files to
/// DOCX before running real extraction.
/// </summary>
public sealed class ReflectionCompanionExtractor : ILocalTextExtractor, IDisposable
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

    public ReflectionCompanionExtractor(string companionAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companionAssemblyPath);
        _companionDirectory = Path.GetDirectoryName(Path.GetFullPath(companionAssemblyPath))
            ?? throw new ArgumentException("The companion assembly path has no directory.", nameof(companionAssemblyPath));
    }

    public async Task<LocalExtractionResult> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var extension = Path.GetExtension(sourcePath);

        if (string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalExtractionResult(0, "error", LocalExtractionErrorCodes.DocLibreOfficeRequired);
        }

        if (!AdapterTypeNames.TryGetValue(extension, out var typeName))
        {
            return new LocalExtractionResult(0, "error", LocalExtractionErrorCodes.ExtractorUnavailable);
        }

        try
        {
            var (instance, extract) = GetAdapter(typeName);
            var task = (Task<string>)extract.Invoke(instance, [sourcePath, cancellationToken])!;
            var text = await task.ConfigureAwait(false);
            return new LocalExtractionResult(Encoding.UTF8.GetByteCount(text), "completed", null);
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
            if (actual is OperationCanceledException)
            {
                throw actual;
            }

            return new LocalExtractionResult(0, "error", LocalExtractionErrorCodes.ExtractorUnavailable);
        }
    }

    public void Dispose() => _loadContext?.Unload();

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
