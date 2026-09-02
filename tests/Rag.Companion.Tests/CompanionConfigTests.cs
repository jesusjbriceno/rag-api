using Rag.Companion.Host;

namespace Rag.Companion.Tests;

public sealed class CompanionConfigTests : IDisposable
{
    private readonly string _dir;

    public CompanionConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TryDelete(_dir);

    [Fact]
    public void Load_reads_all_required_fields()
    {
        var path = WriteConfig(ValidJson());

        var config = CompanionConfig.Load(path);

        Assert.Equal("https://api.test", config.ApiBaseUrl);
        Assert.Equal("https://bff.test", config.CompanionBaseUrl);
        Assert.Equal("companion-1", config.CompanionId);
        Assert.Equal("key-1", config.CompanionKeyId);
        Assert.Equal("secret-1", config.CompanionSecret);
        Assert.Equal("svc-key", config.ServiceClientKeyId);
        Assert.Equal("svc-secret", config.ServiceClientSecret);
        Assert.Equal(["C:\\dropbox"], config.Roots);
    }

    [Fact]
    public void Load_missing_file_throws_config_exception()
    {
        var ex = Assert.Throws<ConfigException>(() => CompanionConfig.Load(Path.Combine(_dir, "nope.json")));
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public void Load_invalid_json_throws_config_exception()
    {
        var path = WriteConfig("{ not valid json");
        Assert.Throws<ConfigException>(() => CompanionConfig.Load(path));
    }

    [Fact]
    public void Load_unknown_field_throws_config_exception()
    {
        var path = WriteConfig(ValidJson().Replace("\"roots\": [\"C:\\\\dropbox\"]", "\"roots\": [\"C:\\\\dropbox\"], \"provider\": \"dropbox\""));
        Assert.Throws<ConfigException>(() => CompanionConfig.Load(path));
    }

    [Fact]
    public void Load_missing_roots_throws_config_exception()
    {
        var json = """
            {
              "apiBaseUrl": "https://api.test",
              "companionBaseUrl": "https://bff.test",
              "companionId": "id",
              "companionKeyId": "key",
              "companionSecret": "secret",
              "serviceClientKeyId": "sk",
              "serviceClientSecret": "ss"
            }
            """;
        var ex = Assert.Throws<ConfigException>(() => CompanionConfig.Load(WriteConfig(json)));
        Assert.Contains("roots", ex.Message);
    }

    [Fact]
    public void Load_does_not_leak_secret_in_error_message()
    {
        const string marker = "SECRET_LEAK_MARKER_9f8e7d6c5b";
        var json = $$"""
            {
              "apiBaseUrl": "https://api.test",
              "companionBaseUrl": "https://bff.test",
              "companionId": "id",
              "companionKeyId": "key",
              "companionSecret": "{{marker}}",
              "serviceClientKeyId": "sk",
              "serviceClientSecret": "ss"
            }
            """;

        var ex = Assert.Throws<ConfigException>(() => CompanionConfig.Load(WriteConfig(json)));

        Assert.DoesNotContain(marker, ex.Message);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "companion.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string ValidJson() => """
        {
          "apiBaseUrl": "https://api.test",
          "companionBaseUrl": "https://bff.test",
          "companionId": "companion-1",
          "companionKeyId": "key-1",
          "companionSecret": "secret-1",
          "serviceClientKeyId": "svc-key",
          "serviceClientSecret": "svc-secret",
          "roots": ["C:\\dropbox"]
        }
        """;

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
