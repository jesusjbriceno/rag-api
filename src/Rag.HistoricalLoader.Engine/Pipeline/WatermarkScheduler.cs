using Rag.HistoricalLoader.Core.Lifecycle;

namespace Rag.HistoricalLoader.Engine.Pipeline;

/// <summary>
/// Bounds locally staged normalized text by byte and count watermarks. A staged key is recorded when
/// extraction completes and released only on commit (a <c>loaded</c> outcome), so staged capacity is
/// never reclaimed before the server confirms the document. The committed watermark therefore advances
/// only after commit. Both watermarks are rehydrated from durable rows after a restart.
/// </summary>
public sealed class WatermarkScheduler
{
    private readonly long _stagedByteWatermark;
    private readonly int _stagedCountWatermark;
    private readonly Dictionary<string, long> _stagedByKey = new(StringComparer.Ordinal);

    /// <summary>Keys already accounted for by this instance, whether staged, committed, or rehydrated.</summary>
    private readonly HashSet<string> _accountedKeys = new(StringComparer.Ordinal);

    public WatermarkScheduler(long stagedByteWatermark, int stagedCountWatermark)
    {
        if (stagedByteWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stagedByteWatermark));
        }

        if (stagedCountWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stagedCountWatermark));
        }

        _stagedByteWatermark = stagedByteWatermark;
        _stagedCountWatermark = stagedCountWatermark;
    }

    public long StagedBytes { get; private set; }

    public int StagedCount { get; private set; }

    public long CommittedBytes { get; private set; }

    public int CommittedCount { get; private set; }

    public bool IsStageWatermarkReached
        => StagedBytes >= _stagedByteWatermark || StagedCount >= _stagedCountWatermark;

    /// <summary>Records staged bytes for a source key; re-staging the same key within a run is a no-op.</summary>
    public void RecordStaged(string sourceKey, long bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        if (!_accountedKeys.Add(sourceKey))
        {
            return;
        }

        _stagedByKey[sourceKey] = bytes;
        StagedBytes += bytes;
        StagedCount++;
    }

    /// <summary>
    /// Releases staged capacity and advances the committed watermark. Returns <c>false</c> when the key
    /// was not staged (and nothing changes).
    /// </summary>
    public bool ReleaseCommitted(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        if (!_stagedByKey.Remove(sourceKey, out var bytes))
        {
            return false;
        }

        StagedBytes -= bytes;
        StagedCount--;
        CommittedBytes += bytes;
        CommittedCount++;
        return true;
    }

    /// <summary>
    /// Rehydrates the staged and committed watermarks from durable rows after a restart. A row flagged
    /// <see cref="WatermarkRow.Committed"/> advances the committed watermark; every other row occupies staged
    /// capacity because its normalized text is still staged locally and was never confirmed. A key already
    /// accounted for by this instance is ignored, so repeated rehydration — and re-processing of a document
    /// whose durable row is re-read — never double counts.
    /// </summary>
    public void Rehydrate(IEnumerable<WatermarkRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        foreach (var row in rows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(row.SourceDocumentKey);
            if (row.StagedBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rows), "Staged bytes cannot be negative.");
            }

            if (!_accountedKeys.Add(row.SourceDocumentKey))
            {
                continue;
            }

            if (row.Committed)
            {
                CommittedBytes += row.StagedBytes;
                CommittedCount++;
                continue;
            }

            _stagedByKey[row.SourceDocumentKey] = row.StagedBytes;
            StagedBytes += row.StagedBytes;
            StagedCount++;
        }
    }

    /// <summary>
    /// Returns the staged byte count currently held for a key. Used to persist the durable staged measurement
    /// that a later restart rehydrates from.
    /// </summary>
    public bool TryGetStagedBytes(string sourceKey, out long bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        return _stagedByKey.TryGetValue(sourceKey, out bytes);
    }
}
