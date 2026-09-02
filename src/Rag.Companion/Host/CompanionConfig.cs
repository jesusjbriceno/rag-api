using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.Companion.Host;

/// <summary>Thrown when the companion configuration is missing, malformed, or invalid.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Administrator-provided run configuration. Secrets are held only in memory and are never written
/// to logs, events, or per-file failure records. Validation messages reference field names, never
/// field values, so a misconfiguration can never leak a credential to the console.
/// </summary>
public sealed record CompanionConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = true,
    };

    /// <summary>Base URL of the data-plane ingestion API (token exchange and TXT ingestion).</summary>
    public string ApiBaseUrl { get; init; } = "";

    /// <summary>Base URL of the BFF companion protocol.</summary>
    public string CompanionBaseUrl { get; init; } = "";

    public string CompanionId { get; init; } = "";

    public string CompanionKeyId { get; init; } = "";

    public string CompanionSecret { get; init; } = "";

    public string ServiceClientKeyId { get; init; } = "";

    public string ServiceClientSecret { get; init; } = "";

    /// <summary>Administrator-selected local root directories; the snapshot is confined to these.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>Absolute path to the pinned LibreOffice <c>soffice.com</c> (26.8 x64) for legacy DOC conversion.</summary>
    public string LibreOfficePath { get; init; } = "soffice.com";

    /// <summary>Path of the local structured log that records final per-file failures.</summary>
    public string FailureLogPath { get; init; } = "companion-failures.jsonl";

    public static CompanionConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"The config file could not be read: '{path}'.", ex);
        }

        CompanionConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<CompanionConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigException(
                "The config file is not valid JSON, is missing required fields, or contains unknown fields.",
                ex);
        }

        if (config is null)
        {
            throw new ConfigException("The config file is empty.");
        }

        Validate(config);
        return config;
    }

    private static void Validate(CompanionConfig config)
    {
        Require(config.ApiBaseUrl, "apiBaseUrl");
        Require(config.CompanionBaseUrl, "companionBaseUrl");
        Require(config.CompanionId, "companionId");
        Require(config.CompanionKeyId, "companionKeyId");
        Require(config.CompanionSecret, "companionSecret");
        Require(config.ServiceClientKeyId, "serviceClientKeyId");
        Require(config.ServiceClientSecret, "serviceClientSecret");

        if (config.Roots is null || config.Roots.Count == 0)
        {
            throw new ConfigException("Config field 'roots' must list at least one directory.");
        }

        if (config.Roots.Any(string.IsNullOrWhiteSpace))
        {
            throw new ConfigException("Config field 'roots' must not contain empty entries.");
        }
    }

    private static void Require(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ConfigException($"Config field '{fieldName}' is required.");
        }
    }
}
