namespace Rag.Companion.Adapters;

/// <summary>
/// Extracts raw UTF-8 text from a single supported source file. Adapters return un-normalized text;
/// canonical normalization is applied afterwards by <see cref="Normalizer"/>.
/// </summary>
public interface ITextAdapter
{
    Task<string> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default);
}
