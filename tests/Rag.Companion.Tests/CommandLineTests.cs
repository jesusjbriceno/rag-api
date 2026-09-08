using Rag.Companion.Host;

namespace Rag.Companion.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly string _dir;

    public CommandLineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cmdline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TryDelete(_dir);

    [Fact]
    public async Task usage_error_returns_exit_2()
    {
        var error = new StringWriter();
        var code = await CommandLine.RunAsync([], stderr: error);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task invalid_config_exits_non_zero_without_leaking_secret()
    {
        const string marker = "SUPER_SECRET_MARKER_1234567890";
        var configPath = Path.Combine(_dir, "companion.json");
        File.WriteAllText(configPath, $$"""
            {
              "apiBaseUrl": "https://api.test",
              "companionBaseUrl": "https://bff.test",
              "companionId": "id",
              "companionKeyId": "key",
              "companionSecret": "{{marker}}",
              "serviceClientKeyId": "sk",
              "serviceClientSecret": "ss"
            }
            """);

        var error = new StringWriter();
        var code = await CommandLine.RunAsync(["run", "--config", configPath], stderr: error);

        Assert.Equal(ExitCodes.ConfigError, code);
        Assert.DoesNotContain(marker, error.ToString());
    }

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
