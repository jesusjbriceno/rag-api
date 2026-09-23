using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.UnitTests.Pipeline;

public sealed class WatermarkSchedulerTests
{
    [Fact]
    public void RecordStaged_IncrementsStagedBytesAndCount()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        scheduler.RecordStaged("a", 300);
        scheduler.RecordStaged("b", 200);

        Assert.Equal(500, scheduler.StagedBytes);
        Assert.Equal(2, scheduler.StagedCount);
    }

    [Fact]
    public void RecordStaged_SameKey_IsIdempotent()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        scheduler.RecordStaged("a", 300);
        scheduler.RecordStaged("a", 400);

        Assert.Equal(300, scheduler.StagedBytes);
        Assert.Equal(1, scheduler.StagedCount);
    }

    [Fact]
    public void IsStageWatermarkReached_ByBytes()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 10, stagedCountWatermark: 100);

        Assert.False(scheduler.IsStageWatermarkReached);
        scheduler.RecordStaged("a", 11);
        Assert.True(scheduler.IsStageWatermarkReached);
    }

    [Fact]
    public void IsStageWatermarkReached_ByCount()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 2);

        scheduler.RecordStaged("a", 1);
        Assert.False(scheduler.IsStageWatermarkReached);
        scheduler.RecordStaged("b", 1);
        Assert.True(scheduler.IsStageWatermarkReached);
    }

    [Fact]
    public void ReleaseCommitted_ReleasesStagedAndAdvancesCommittedOnly()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        scheduler.RecordStaged("a", 300);
        var released = scheduler.ReleaseCommitted("a");

        Assert.True(released);
        Assert.Equal(0, scheduler.StagedBytes);
        Assert.Equal(0, scheduler.StagedCount);
        Assert.Equal(300, scheduler.CommittedBytes);
        Assert.Equal(1, scheduler.CommittedCount);
    }

    [Fact]
    public void ReleaseCommitted_UnknownKey_AdvancesNothing()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        Assert.False(scheduler.ReleaseCommitted("missing"));
        Assert.Equal(0, scheduler.CommittedBytes);
        Assert.Equal(0, scheduler.CommittedCount);
    }


    // --- Durable rehydration after restart -----------------------------------

    [Fact]
    public void Rehydrate_RestoresCommittedWatermark_FromCommittedRows()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        scheduler.Rehydrate(new[]
        {
            new WatermarkRow("loaded-1", Committed: true, StagedBytes: 400),
            new WatermarkRow("loaded-2", Committed: true, StagedBytes: 200),
        });

        Assert.Equal(600, scheduler.CommittedBytes);
        Assert.Equal(2, scheduler.CommittedCount);
        Assert.Equal(0, scheduler.StagedBytes);
        Assert.Equal(0, scheduler.StagedCount);
    }

    [Fact]
    public void Rehydrate_RestoresStagedWatermark_FromUncommittedRows_AndStaysReleasable()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        scheduler.Rehydrate(new[]
        {
            new WatermarkRow("staged-1", Committed: false, StagedBytes: 300),
            new WatermarkRow("staged-2", Committed: false, StagedBytes: 100),
        });

        Assert.Equal(400, scheduler.StagedBytes);
        Assert.Equal(2, scheduler.StagedCount);
        Assert.Equal(0, scheduler.CommittedBytes);
        Assert.Equal(0, scheduler.CommittedCount);

        // A rehydrated staged key is still released by the commit that follows the restart.
        Assert.True(scheduler.ReleaseCommitted("staged-1"));
        Assert.Equal(100, scheduler.StagedBytes);
        Assert.Equal(1, scheduler.StagedCount);
        Assert.Equal(300, scheduler.CommittedBytes);
        Assert.Equal(1, scheduler.CommittedCount);
    }

    [Fact]
    public void Rehydrate_UncommittedRow_TriggersStageWatermark()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 500, stagedCountWatermark: 100);

        scheduler.Rehydrate(new[] { new WatermarkRow("staged-1", Committed: false, StagedBytes: 500) });

        Assert.True(scheduler.IsStageWatermarkReached);
    }

    [Fact]
    public void Rehydrate_RepeatedWithSameRows_DoesNotDoubleCount()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);
        var rows = new[]
        {
            new WatermarkRow("loaded-1", Committed: true, StagedBytes: 400),
            new WatermarkRow("staged-1", Committed: false, StagedBytes: 100),
        };

        scheduler.Rehydrate(rows);
        scheduler.Rehydrate(rows);

        Assert.Equal(400, scheduler.CommittedBytes);
        Assert.Equal(1, scheduler.CommittedCount);
        Assert.Equal(100, scheduler.StagedBytes);
        Assert.Equal(1, scheduler.StagedCount);
    }

    [Fact]
    public void Rehydrate_KeyAlreadyAccountedInProcess_IsIgnored()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        scheduler.RecordStaged("a", 300);
        Assert.True(scheduler.ReleaseCommitted("a"));

        // The durable row for the key just committed in this instance must not be added a second time.
        scheduler.Rehydrate(new[] { new WatermarkRow("a", Committed: true, StagedBytes: 300) });

        Assert.Equal(300, scheduler.CommittedBytes);
        Assert.Equal(1, scheduler.CommittedCount);
        Assert.Equal(0, scheduler.StagedBytes);
        Assert.Equal(0, scheduler.StagedCount);
    }

    [Fact]
    public void Rehydrate_NullRows_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10).Rehydrate(null!));

    [Fact]
    public void Rehydrate_UnmeasuredStagedRow_OccupiesCapacityWithoutInventingBytes()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: long.MaxValue, stagedCountWatermark: 1);

        scheduler.Rehydrate(new[] { new WatermarkRow("unmeasured", Committed: false, StagedBytes: 0) });

        // The staged slot is restored so the bounded staging watermark still holds; the unknown byte count
        // is never fabricated.
        Assert.Equal(1, scheduler.StagedCount);
        Assert.Equal(0, scheduler.StagedBytes);
        Assert.True(scheduler.IsStageWatermarkReached);

        Assert.True(scheduler.ReleaseCommitted("unmeasured"));
        Assert.Equal(1, scheduler.CommittedCount);
        Assert.Equal(0, scheduler.CommittedBytes);
    }

        [Fact]
        public void TryGetStagedBytes_ReturnsRecordedValue_OnlyForStagedKeys()
    {
        var scheduler = new WatermarkScheduler(stagedByteWatermark: 1000, stagedCountWatermark: 10);

        scheduler.RecordStaged("a", 300);
        scheduler.ReleaseCommitted("a");

        Assert.False(scheduler.TryGetStagedBytes("a", out _));
        Assert.False(scheduler.TryGetStagedBytes("missing", out _));

        scheduler.Rehydrate(new[] { new WatermarkRow("staged-1", Committed: false, StagedBytes: 42) });

        Assert.True(scheduler.TryGetStagedBytes("staged-1", out var bytes));
        Assert.Equal(42, bytes);
    }
}
