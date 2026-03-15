using TraceViewer.SessionModel;

namespace TraceViewer.Tests;

public sealed class FrameStoreTests
{
    [Fact]
    public void BeginAndEndFrame_AssignsIndexAndSupportsTimestampLookup()
    {
        var store = new FrameStore();
        store.BeginFrame(FrameType.Game, 10.0);
        store.EndFrame(FrameType.Game, 16.0);
        store.BeginFrame(FrameType.Game, 16.0);
        store.EndFrame(FrameType.Game, 33.0);

        var gameFrames = store.GetSeries(FrameType.Game);

        Assert.Equal<ulong>(2, (ulong)gameFrames.Frames.Count);
        Assert.Equal<ulong>(0, gameFrames.Frames[0].Index);
        Assert.Equal<ulong>(1, gameFrames.Frames[1].Index);
        Assert.Equal(0u, gameFrames.GetFrameNumberForTimestamp(10.1));
        Assert.Equal(1u, gameFrames.GetFrameNumberForTimestamp(20.0));
        Assert.True(gameFrames.TryGetFrameFromTime(20.0, out var frame));
        Assert.NotNull(frame);
        Assert.Equal<ulong>(1, frame!.Index);
    }

    [Fact]
    public void EnumerateIntersecting_IncludesPreviousFrameWhenItCrossesStartBoundary()
    {
        var store = new FrameStore();
        store.BeginFrame(FrameType.Game, 10.0);
        store.EndFrame(FrameType.Game, 20.0);
        store.BeginFrame(FrameType.Game, 20.0);
        store.EndFrame(FrameType.Game, 30.0);

        var visibleFrames = store.GetSeries(FrameType.Game).EnumerateIntersecting(19.5, 25.0);

        Assert.Collection(
            visibleFrames,
            frame => Assert.Equal<ulong>(0, frame.Index),
            frame => Assert.Equal<ulong>(1, frame.Index));
    }
}
