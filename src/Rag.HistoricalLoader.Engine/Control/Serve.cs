using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Configuration;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Engine.Api;
using Rag.HistoricalLoader.Engine.Extraction;
using Rag.HistoricalLoader.Engine.Pipeline;
using Rag.HistoricalLoader.Engine.Security;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The validated composition facts of one <c>serve</c> invocation. They are exactly what the command line
/// carries — the database path, the installation lock path, and the target collection — so a caller can never
/// select a pipe endpoint, an extractor, an API target, or a credential through the plan.
/// </summary>
public sealed record ControlServePlan
{
    /// <summary>The loader database the supervised host opens after it holds the installation lock.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>The OS-backed exclusive installation lock path.</summary>
    public required string LockFilePath { get; init; }

    /// <summary>The target collection recorded on a started run.</summary>
    public required string CollectionId { get; init; }
}

/// <summary>
/// The explicit <c>serve</c> composition entry point. It validates its own arguments, asks the boundary whether
/// this platform can be served, and only then composes the supervised host (installation lock → store →
/// supervised service → pipe).
/// </summary>
/// <remarks>
/// The platform gate runs first and fails closed with the contract's stable <c>platform_not_supported</c> code
/// <b>before</b> the installation lock, the store, every credential, and any ingestion: on a platform this
/// boundary cannot serve, the invocation creates nothing at all. The pipe opens only under this subcommand, and
/// only through the transport's explicit <c>Start</c>.
/// </remarks>
public static class ControlServeCommand
{
    /// <summary>The exit code of an invocation this build cannot serve.</summary>
    public const int NotSupportedExitCode = 3;

    /// <summary>The exit code of a malformed invocation.</summary>
    public const int UsageExitCode = 2;

    /// <summary>The exit code of an invocation that cannot take the installation.</summary>
    public const int ConflictExitCode = 4;

    /// <summary>The documented usage line of the subcommand.</summary>
    public const string UsageLine =
        "  serve --db <path> [--lock <path>] [--collection <id>]";

    /// <summary>
    /// The non-secret composition facts of the production pipeline that the command line deliberately does not
    /// carry. The two <i>secrets</i> never appear here: they stay in the Windows Credential Manager and are read
    /// only when a request needs them.
    /// </summary>
    public static class CompositionVariables
    {
        /// <summary>The Companion assembly the engine extracts with.</summary>
        public const string CompanionAssembly = "RAG_HISTORICAL_LOADER_COMPANION_ASSEMBLY";

        /// <summary>The historical API base URI (the client-stack convention recorded in the deployment docs).</summary>
        public const string ApiBaseUri = "RAG_API_BASE_URL";

        /// <summary>The API collection GUID the ingestion targets.</summary>
        public const string ApiCollectionId = "RAG_HISTORICAL_LOADER_API_COLLECTION_ID";
    }

    /// <summary>
    /// Runs the <c>serve</c> subcommand and returns its exit code. Existing commands (<c>inventory</c>,
    /// <c>select-sample</c>, <c>benchmark extraction</c>) are untouched by this entry point.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!TryPlan(args, out var plan, out var planError, out var planExitCode))
        {
            Console.Error.WriteLine($"serve: {planError}.");
            return planExitCode;
        }

        var resolvedPlan = plan!;

        // The composition facts are validated before the lock and the store, so a half-configured machine does
        // not take the installation over and then fail to ingest.
        if (!ControlServeComposition.TryCompose(out var composition, out var compositionError))
        {
            Console.Error.WriteLine($"serve: {compositionError}.");
            return UsageExitCode;
        }

        using var owned = composition!;
        if (!ControlPipeTransportFactory.TryCreate(out var transport, out var endpoint, out var transportError)
            || transport is null
            || endpoint is null)
        {
            // Fail closed before the lock, the store, and every credential: an engine that cannot be reached by
            // a client must not take the installation over, and it must not start ingestion on its own.
            Console.Error.WriteLine($"{transportError}: this build serves no control transport.");
            return NotSupportedExitCode;
        }

        // The host owns the boundary lifetime; wrapping it here keeps the production pipe behind the same
        // explicit Start the supervised composition performs.
        var pipe = new NamedPipeHost(transport, endpoint);

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, cancel) =>
        {
            // The drain, not the console, decides how the run ends: the first interrupt requests shutdown.
            cancel.Cancel = true;
            shutdown.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            return await ServeAsync(
                resolvedPlan,
                pipe,
                owned.Extractor,
                owned.Api,
                owned.Pipeline,
                shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            pipe.Stop();
        }
    }

    /// <summary>
    /// Validates one <c>serve</c> invocation without opening anything, or reports the stable code and exit code
    /// of the invocation it refused. A caller-selected pipe endpoint is refused as a usage error, and no
    /// argument can smuggle a path into the endpoint.
    /// </summary>
    public static bool TryPlan(
        string[] args,
        out ControlServePlan? plan,
        out string errorCode,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        plan = null;

        if (args.Length == 0 || !string.Equals(args[0], "serve", StringComparison.Ordinal))
        {
            errorCode = "usage";
            exitCode = UsageExitCode;
            return false;
        }

        var options = new HistoricalLoaderOptions();
        var databasePath = options.DatabasePath;
        string? lockFilePath = null;
        string? collectionId = null;

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--db" when index + 1 < args.Length:
                    databasePath = args[++index];
                    break;
                case "--lock" when index + 1 < args.Length:
                    lockFilePath = args[++index];
                    break;
                case "--collection" when index + 1 < args.Length:
                    collectionId = args[++index];
                    break;

                // An endpoint is a fact of the boundary, never of the invocation: a caller cannot select the
                // pipe name, its path, or any kernel object of its own.
                case "--pipe" or "--pipe-name" or "--pipe-path" or "--endpoint":
                    Console.Error.WriteLine($"serve: '{args[index]}' is refused: the pipe endpoint is derived by the engine.");
                    errorCode = "usage";
                    exitCode = UsageExitCode;
                    return false;

                default:
                    Console.Error.WriteLine($"serve: unknown or incomplete argument '{args[index]}'.");
                    errorCode = "usage";
                    exitCode = UsageExitCode;
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            Console.Error.WriteLine("serve requires --db <path>.");
            errorCode = "usage";
            exitCode = UsageExitCode;
            return false;
        }

        // The installation lock and the database live side by side unless the operator selected the lock path.
        var resolvedLock = lockFilePath;
        if (string.IsNullOrWhiteSpace(resolvedLock))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            resolvedLock = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, "historical-loader.lock");
        }

        // Last, because it is the one gate the arguments cannot satisfy: a platform this boundary cannot serve
        // fails closed with the contract's stable code, before the lock, the store, and every credential.
        if (!ControlPipeTransportFactory.IsPlatformSupported)
        {
            errorCode = ControlErrorCodes.PlatformNotSupported;
            exitCode = NotSupportedExitCode;
            return false;
        }

        errorCode = string.Empty;
        exitCode = 0;
        plan = new ControlServePlan
        {
            DatabasePath = Path.GetFullPath(databasePath),
            LockFilePath = Path.GetFullPath(resolvedLock),
            CollectionId = string.IsNullOrWhiteSpace(collectionId) ? "legacy" : collectionId,
        };

        return true;
    }

    /// <summary>
    /// Composes the full supervised host over the given transport: the OS-backed installation lock first, then
    /// the store, then the supervised service, and finally the pipe. A second instance fails closed on the
    /// installation lock before its own pipe is ever opened, and a boundary that cannot own its endpoint is not
    /// claimed as a listener.
    /// </summary>
    /// <returns>
    /// <see cref="ConflictExitCode"/> when the installation lock is held or the pipe cannot be owned, and the
    /// success code 0 after a cancellation-requested drain.
    /// </returns>
    public static async Task<int> ServeAsync(
        ControlServePlan plan,
        IControlTransport transport,
        IExtractor extractor,
        IHistoricalApiClient api,
        PipelineOptions pipeline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pipeline);

        var options = new ControlHostOptions
        {
            DatabasePath = plan.DatabasePath,
            LockFilePath = plan.LockFilePath,
            Service = new ControlServiceOptions
            {
                EngineInstanceId = $"engine-instance-{Guid.NewGuid():N}",
                CollectionId = plan.CollectionId,
            },
        };

        ControlHost host;
        try
        {
            host = await ControlHost.OpenAsync(options, transport, extractor, api, pipeline, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ControlInstallationLockHeldException)
        {
            // A second instance fails closed on the installation lock, before its own pipe is opened: no
            // listener is claimed and nothing is dispatched.
            Console.Error.WriteLine(
                $"serve: another engine instance holds the installation lock '{plan.LockFilePath}'.");
            return ConflictExitCode;
        }

        await using (host.ConfigureAwait(false))
        {
            if (!host.IsListening)
            {
                // The endpoint could not be owned. Serve drains nothing and claims nothing: an engine that
                // cannot be reached must not hold the installation while pretending to serve it.
                Console.Error.WriteLine("serve: this engine could not own its pipe endpoint.");
                return ConflictExitCode;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The operator asked the host to stop.
            }

            // The drain carries its own bounded timeout from the service options, so the already-cancelled
            // shutdown token must not shorten it: a timed-out drain leaves recovery work, never a fabricated
            // pause. The host then stops listening and releases the installation lock.
            await host.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
    }
}

/// <summary>
/// The production collaborators of <c>serve</c>: the real extractor, the real historical API client wired to
/// the Windows Credential Manager, and the recorded pipeline bounds. Composition only <b>describes</b> those
/// facts; it reads no credential and opens no database, and the credential providers run lazily, per request.
/// </summary>
internal sealed class ControlServeComposition : IDisposable
{
    private readonly HttpClient? _http;

    private ControlServeComposition(
        CompanionReflectionExtractor extractor,
        HistoricalApiClient api,
        PipelineOptions pipeline,
        HttpClient? http)
    {
        Extractor = extractor;
        Api = api;
        Pipeline = pipeline;
        _http = http;
    }

    internal IExtractor Extractor { get; }

    internal IHistoricalApiClient Api { get; }

    internal PipelineOptions Pipeline { get; }

    /// <summary>
    /// Validates the recorded environment facts and builds the production collaborators. Missing or malformed
    /// facts fail closed with a message naming the exact variable; no fact is guessed and no secret is read.
    /// </summary>
    internal static bool TryCompose(out ControlServeComposition? composition, out string errorCode)
    {
        composition = null;

        var companionAssembly = Environment.GetEnvironmentVariable(ControlServeCommand.CompositionVariables.CompanionAssembly);
        if (string.IsNullOrWhiteSpace(companionAssembly))
        {
            errorCode = $"'{ControlServeCommand.CompositionVariables.CompanionAssembly}' is required";
            return false;
        }

        var baseUriText = Environment.GetEnvironmentVariable(ControlServeCommand.CompositionVariables.ApiBaseUri);
        if (string.IsNullOrWhiteSpace(baseUriText) || !Uri.TryCreate(baseUriText, UriKind.Absolute, out var baseUri))
        {
            errorCode = $"'{ControlServeCommand.CompositionVariables.ApiBaseUri}' must be an absolute URI";
            return false;
        }

        var collectionText = Environment.GetEnvironmentVariable(ControlServeCommand.CompositionVariables.ApiCollectionId);
        if (string.IsNullOrWhiteSpace(collectionText) || !Guid.TryParse(collectionText, out var apiCollectionId))
        {
            errorCode = $"'{ControlServeCommand.CompositionVariables.ApiCollectionId}' must be a GUID";
            return false;
        }

        var credentials = new WindowsCredentialManager();
        var options = new HistoricalApiClientOptions(
            baseUri,
            apiCollectionId,
            _ => Task.FromResult(credentials.ReadRagCredential()
                ?? throw new InvalidOperationException("The RAG service credential is not stored for this user.")),
            _ => Task.FromResult(credentials.ReadCloudflareToken()
                ?? throw new InvalidOperationException("The Cloudflare service token is not stored for this user.")));

        var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(10) };
        composition = new ControlServeComposition(
            new CompanionReflectionExtractor(companionAssembly),
            new HistoricalApiClient(http, options),
            RecordedPipelineOptions,
            http);
        errorCode = string.Empty;
        return true;
    }

    /// <summary>
    /// The recorded pipeline bounds: concurrency 1, exactly as the bounded-pipeline acceptance evidence records
    /// it. The staged byte/count caps are deliberately **not** chosen here — the design defers those numeric
    /// values to inventory and operator disk-budget evidence — so this composition carries the same unbounded
    /// sentinels the unit's proofs used rather than inventing a cap.
    /// </summary>
    private static PipelineOptions RecordedPipelineOptions { get; } =
        new(Concurrency: 1, StagedByteWatermark: long.MaxValue, StagedCountWatermark: int.MaxValue);

    /// <inheritdoc />
    public void Dispose()
    {
        (Extractor as IDisposable)?.Dispose();
        _http?.Dispose();
    }
}
