namespace Rag.Domain;

public sealed record EmbeddingProfile
{
    public static readonly EmbeddingProfile Default = new(
        "llama.cpp",
        "hf://Qwen/Qwen3-Embedding-0.6B-GGUF@370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf",
        "sha256:06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439",
        1_024);

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
