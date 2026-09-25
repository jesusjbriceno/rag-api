using System.Net;
using System.Text;
using Rag.Companion.Adapters;
using Rag.Companion.Ingestion;
using Rag.Companion.Protocol;
using Rag.Companion.Snapshot;

namespace Rag.Companion.Host;

/// <summary>
/// Executes one bounded snapshot import: acquire the BFF lease, report a running event before
/// scanning, process each supported file sequentially, report progress at most every heartbeat
/// interval, and report a terminal succeeded or failed event. Final per-file failures are recorded
/// to a local structured log carrying only an error code (never a path or secret).
/// </summary>
public sealed class CompanionRun
{
    private readonly CompanionConfig _config;
    private readonly CompanionHttpClient _companion;
    private readonly IngestionClient _ingestion;
    private readonly AdapterRegistry _registry;
    private readonly ConfinedSnapshotService _snapshot;
    private readonly RetryPolicy _retry;
    private readonly TimeProvider _clock;
    private readonly TextWriter _output;

    public CompanionRun(
        CompanionConfig config,
        CompanionHttpClient companion,
        IngestionClient ingestion,
        AdapterRegistry registry,
        TimeProvider? clock = null,
        TextWriter? output = null,
        ConfinedSnapshotService? snapshot = null,
        RetryPolicy? retry = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _companion = companion ?? throw new ArgumentNullException(nameof(companion));
        _ingestion = ingestion ?? throw new ArgumentNullException(nameof(ingestion));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _snapshot = snapshot ?? new ConfinedSnapshotService();
        _retry = retry ?? new RetryPolicy();
        _clock = clock ?? TimeProvider.System;
        _output = output ?? Console.Out;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var lease = await _companion.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            // The lease endpoint returned 204: no snapshot work is available. Scan nothing, ingest nothing.
            _output.WriteLine("No snapshot work available (the companion lease returned no work).");
            return ExitCodes.Success;
        }

        long sequence = lease.NextSequence;
        await SendRunningAsync(lease, sequence++, 0, 0, cancellationToken).ConfigureAwait(false);

        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "rag-companion-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);

        StreamWriter? failureLog = null;
        FailureRecorder? recorder = null;
        try
        {
            var copier = new HandleConfinedCopier(_config.Roots, snapshotDirectory, _clock);
            var files = _snapshot.Discover(_config.Roots);

            long processed = 0;
            long failed = 0;
            var lastHeartbeat = _clock.GetUtcNow();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var snapshotPath = await copier.CopyAsync(file, cancellationToken).ConfigureAwait(false);
                    var normalizedText = await _registry.ExtractNormalizedTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
                    await _ingestion.SubmitTextAsync(lease.CollectionId, Path.GetFileName(file), normalizedText, cancellationToken).ConfigureAwait(false);
                    processed++;
                }
                catch (Exception ex) when (IsFinalFileFailure(ex))
                {
                    failed++;
                    recorder ??= CreateRecorder(ref failureLog);
                    await recorder.RecordAsync(ErrorCodeFor(ex), cancellationToken).ConfigureAwait(false);
                }

                var now = _clock.GetUtcNow();
                if (now - lastHeartbeat >= CompanionHttpClient.HeartbeatInterval)
                {
                    await SendRunningAsync(lease, sequence++, processed, failed, cancellationToken).ConfigureAwait(false);
                    lastHeartbeat = now;
                }
            }

            var terminalState = failed == 0 ? CompanionState.Succeeded : CompanionState.Failed;
            await SendTerminalAsync(lease, sequence, terminalState, processed, failed, cancellationToken).ConfigureAwait(false);

            _output.WriteLine($"Import finished: {processed} processed, {failed} failed.");
            return failed == 0 ? ExitCodes.Success : ExitCodes.TerminalFailure;
        }
        finally
        {
            if (failureLog is not null)
            {
                await failureLog.DisposeAsync().ConfigureAwait(false);
            }

            TryDelete(snapshotDirectory);
        }
    }

    private FailureRecorder CreateRecorder(ref StreamWriter? failureLog)
    {
        failureLog = new StreamWriter(_config.FailureLogPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new FailureRecorder(failureLog);
    }

    private Task SendRunningAsync(LeaseResponse lease, long sequence, long processed, long failed, CancellationToken cancellationToken) =>
        SendEventWithRetryAsync(lease, EventFor(lease, sequence, CompanionState.Running, processed, failed, errorCode: null), cancellationToken);

    private Task SendTerminalAsync(LeaseResponse lease, long sequence, CompanionState state, long processed, long failed, CancellationToken cancellationToken) =>
        SendEventWithRetryAsync(lease, EventFor(lease, sequence, state, processed, failed, errorCode: null), cancellationToken);

    private static EventRequest EventFor(LeaseResponse lease, long sequence, CompanionState state, long processed, long failed, string? errorCode) =>
        new()
        {
            LeaseId = lease.LeaseId,
            EventId = $"evt-{sequence}",
            Sequence = sequence,
            State = state,
            Processed = processed,
            Failed = failed,
            ErrorCode = errorCode,
        };

    private Task SendEventWithRetryAsync(LeaseResponse lease, EventRequest request, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(
            token => _companion.SendEventAsync(lease.JobId, request, token),
            IsTransientCompanionFailure,
            cancellationToken);

    private static bool IsFinalFileFailure(Exception ex) =>
        ex is ExtractionException or IngestionException or IOException or UnauthorizedAccessException;

    private static string ErrorCodeFor(Exception ex) => ex switch
    {
        ExtractionException extraction => extraction.ErrorCode,
        IngestionException ingestion => ingestion.ErrorCode,
        _ => "file_processing_failed",
    };

    private static bool IsTransientCompanionFailure(Exception ex) => ex switch
    {
        CompanionHttpException { StatusCode: var code } when IsTransientStatus(code) => true,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false,
    };

    private static bool IsTransientStatus(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)code >= 500;

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
