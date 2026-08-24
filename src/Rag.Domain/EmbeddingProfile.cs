namespace Rag.Domain;

public sealed record EmbeddingProfile
{
    public static readonly EmbeddingProfile Default = new("ollama", "qwen3-embedding:0.6b", "0.6b", 1_024);

    public EmbeddingProfile(string provider, string model, string version, int dimensions)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Trim().Length > 100)
        {
            throw new ArgumentException("An embedding provider containing at most 100 characters is required.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(model) || model.Trim().Length > 200 || model.Trim().EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An explicit embedding model version containing at most 200 characters is required.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(version) || version.Trim().Length > 100 || string.Equals(version.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An explicit embedding version containing at most 100 characters is required.", nameof(version));
        }

        if (dimensions is < 1 or > 16_000)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Embedding dimensions must be between 1 and 16,000.");
        }

        Provider = provider.Trim();
        Model = model.Trim();
        Version = version.Trim();
        Dimensions = dimensions;
    }

    public string Provider { get; }

    public string Model { get; }

    public string Version { get; }

    public int Dimensions { get; }
}
