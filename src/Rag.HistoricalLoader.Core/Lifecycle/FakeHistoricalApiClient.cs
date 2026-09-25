namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// Deterministic in-memory <see cref="IHistoricalApiClient"/> for state-machine and retry tests. It
/// succeeds by default, can be scripted to return transient/unknown/auth/contract outcomes, and is
/// idempotent by <see cref="ApiOperation.IdempotencyKey"/> for successful mutations (a replayed key
/// returns the canonical success without a new remote side effect).
/// </summary>
public sealed class FakeHistoricalApiClient : IHistoricalApiClient
{
    private readonly object _gate = new();
    private readonly Queue<ApiOutcome> _script = new();
    private readonly Queue<bool> _pollScript = new();
    private readonly Dictionary<string, ApiOperationResult> _results = new(StringComparer.Ordinal);
    private int _nextId;

    public int OperationCalls { get; private set; }

    public int PollCalls { get; private set; }

    /// <summary>Scripts the next mutation (reserve/upload/commit) to return <paramref name="outcome"/>.</summary>
    public void ScriptNext(ApiOutcome outcome)
    {
        lock (_gate)
        {
            _script.Enqueue(outcome);
        }
    }

    /// <summary>Scripts the next poll to report <c>pending</c> (true) or completed (false).</summary>
    public void ScriptPoll(bool pending)
    {
        lock (_gate)
        {
            _pollScript.Enqueue(pending);
        }
    }

    public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        => ExecuteMutationAsync(operation, "reserve", cancellationToken);

    public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        => ExecuteMutationAsync(operation, "upload", cancellationToken);

    public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        => ExecuteMutationAsync(operation, "commit", cancellationToken);

    public Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PollCalls++;
            var pending = _pollScript.Count > 0 ? _pollScript.Dequeue() : false;
            return Task.FromResult(new ApiOperationResult(
                ApiOutcome.Success,
                RemoteId: pending ? null : "op-1",
                Pending: pending));
        }
    }

    private Task<ApiOperationResult> ExecuteMutationAsync(ApiOperation operation, string kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_results.TryGetValue(operation.IdempotencyKey, out var existing))
            {
                return Task.FromResult(existing);
            }

            OperationCalls++;

            var outcome = _script.Count > 0 ? _script.Dequeue() : ApiOutcome.Success;
            var result = outcome == ApiOutcome.Success
                ? new ApiOperationResult(ApiOutcome.Success, RemoteId: $"{kind}-{++_nextId}")
                : new ApiOperationResult(outcome, RemoteId: null, ErrorCode: MapErrorCode(outcome));

            if (outcome == ApiOutcome.Success)
            {
                _results[operation.IdempotencyKey] = result;
            }

            return Task.FromResult(result);
        }
    }

    private static string? MapErrorCode(ApiOutcome outcome) => outcome switch
    {
        ApiOutcome.TransientFailure => "transient_failure",
        ApiOutcome.UnknownOutcome => "unknown_outcome",
        ApiOutcome.AuthFailure => "blocked_auth",
        ApiOutcome.ContractFailure => "contract_conflict",
        _ => null,
    };
}
