using System.Net.Http;
using Rag.Companion.Adapters;
using Rag.Companion.Ingestion;
using Rag.Companion.LibreOffice;
using Rag.Companion.Protocol;

namespace Rag.Companion.Host;

/// <summary>Parses the command line, loads configuration, and maps the run outcome to an exit code.</summary>
public static class CommandLine
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        var output = stdout ?? Console.Out;
        var error = stderr ?? Console.Error;

        if (args.Length != 3 ||
            !string.Equals(args[0], "run", StringComparison.Ordinal) ||
            !string.Equals(args[1], "--config", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: Rag.Companion run --config <path>");
            return ExitCodes.Usage;
        }

        CompanionConfig config;
        try
        {
            config = CompanionConfig.Load(args[2]);
        }
        catch (ConfigException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCodes.ConfigError;
        }

        try
        {
            return await CreateRun(config, output).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("The import was cancelled.");
            return ExitCodes.TerminalFailure;
        }
        catch (Exception ex) when (ex is CompanionHttpException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return ExitCodes.TerminalFailure;
        }
    }

    internal static CompanionRun CreateRun(CompanionConfig config, TextWriter output)
    {
        var clock = TimeProvider.System;
        var companion = new CompanionHttpClient(
            new HttpClient(),
            new CompanionCredentials(config.CompanionBaseUrl, config.CompanionId, config.CompanionKeyId, config.CompanionSecret),
            clock);
        var ingestion = new IngestionClient(
            new HttpClient(),
            config.ApiBaseUrl,
            config.ServiceClientKeyId,
            config.ServiceClientSecret,
            clock);
        var registry = AdapterRegistry.CreateDefault(new LibreOfficeRunner(config.LibreOfficePath));
        return new CompanionRun(config, companion, ingestion, registry, clock, output);
    }
}
