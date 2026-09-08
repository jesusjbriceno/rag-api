namespace Rag.Companion.Adapters;

/// <summary>Extracts Markdown files (<c>.md</c>) by reading them as strict UTF-8.</summary>
public sealed class MarkdownAdapter : ITextAdapter
{
    public Task<string> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Utf8TextFile.ReadAllText(sourcePath));
    }
}
