using TraceViewer.Query;
using TraceViewer.SessionModel;

namespace TraceViewer.Tests;

public sealed class CpuCallTreeBuilderTests
{
    [Fact]
    public void BuildForFrame_ClipsScopesToFrameAndSortsThreadsByVisibleTime()
    {
        var session = new TraceSession();
        var frame = session.Frames.BeginFrame(FrameType.Game, 10.0);
        session.Frames.EndFrame(FrameType.Game, 20.0);

        session.Threads.AddGameThread(1);
        session.Threads.AddOrUpdateThread(2, "WorkerThread", ProfilerThreadPriority.Normal);

        var tickTimer = session.Timers.AddTimer("Tick", null, 0, TimerType.CpuScope);
        var updateTimer = session.Timers.AddTimer("Update", null, 0, TimerType.CpuScope);
        var workerTimer = session.Timers.AddTimer("WorkerJob", null, 0, TimerType.CpuScope);

        var gameTimeline = session.CpuTimelines.GetOrCreateTimeline(1);
        gameTimeline.AddEvent(new TimelineEvent(8.0, true, TimerRef.ForTimer(tickTimer)));
        gameTimeline.AddEvent(new TimelineEvent(12.0, true, TimerRef.ForTimer(updateTimer)));
        gameTimeline.AddEvent(new TimelineEvent(18.0, false, TimerRef.ForTimer(updateTimer)));
        gameTimeline.AddEvent(new TimelineEvent(22.0, false, TimerRef.ForTimer(tickTimer)));

        var workerTimeline = session.CpuTimelines.GetOrCreateTimeline(2);
        workerTimeline.AddEvent(new TimelineEvent(11.0, true, TimerRef.ForTimer(workerTimer)));
        workerTimeline.AddEvent(new TimelineEvent(13.0, false, TimerRef.ForTimer(workerTimer)));

        var builder = new CpuCallTreeBuilder();
        var threads = builder.BuildForFrame(session, frame);

        Assert.Equal(2, threads.Count);
        Assert.Equal("GameThread", threads[0].Thread.Name);
        Assert.Equal("WorkerThread", threads[1].Thread.Name);

        var root = Assert.Single(threads[0].Roots);
        Assert.Equal("Tick", root.Name);
        Assert.True(root.StartedBeforeFrame);
        Assert.True(root.EndedAfterFrame);
        Assert.Equal(10_000.0, root.InclusiveMilliseconds, 3);
        Assert.Equal(4_000.0, root.ExclusiveMilliseconds, 3);
        Assert.Equal(6_000.0, root.ChildrenMilliseconds, 3);

        var child = Assert.Single(root.Children);
        Assert.Equal("Update", child.Name);
        Assert.False(child.StartedBeforeFrame);
        Assert.False(child.EndedAfterFrame);
        Assert.Equal(6_000.0, child.InclusiveMilliseconds, 3);
    }

    [Fact]
    public void BuildForFrame_AggregatesSequentialSiblingScopesByTimer()
    {
        var session = new TraceSession();
        var frame = session.Frames.BeginFrame(FrameType.Game, 0.0);
        session.Frames.EndFrame(FrameType.Game, 10.0);

        session.Threads.AddGameThread(1);

        var rootTimer = session.Timers.AddTimer("Tick", null, 0, TimerType.CpuScope);
        var workTimer = session.Timers.AddTimer("Task", null, 0, TimerType.CpuScope);

        var timeline = session.CpuTimelines.GetOrCreateTimeline(1);
        timeline.AddEvent(new TimelineEvent(0.0, true, TimerRef.ForTimer(rootTimer)));
        timeline.AddEvent(new TimelineEvent(1.0, true, TimerRef.ForTimer(workTimer)));
        timeline.AddEvent(new TimelineEvent(2.0, false, TimerRef.ForTimer(0)));
        timeline.AddEvent(new TimelineEvent(3.0, true, TimerRef.ForTimer(workTimer)));
        timeline.AddEvent(new TimelineEvent(4.0, false, TimerRef.ForTimer(0)));
        timeline.AddEvent(new TimelineEvent(5.0, false, TimerRef.ForTimer(0)));

        var builder = new CpuCallTreeBuilder();
        var threads = builder.BuildForFrame(session, frame);

        var root = Assert.Single(Assert.Single(threads).Roots);
        var child = Assert.Single(root.Children);

        Assert.Equal("Task", child.Name);
        Assert.Equal(2_000.0, child.InclusiveMilliseconds, 3);
        Assert.Equal(2_000.0, child.ExclusiveMilliseconds, 3);
        Assert.Equal(1.0, child.StartTime, 3);
        Assert.Equal(4.0, child.EndTime, 3);
    }

    [Fact]
    public void BuildForFrame_HandlesDeeplyNestedScopesWithoutRecursiveOverflow()
    {
        const int depth = 3000;

        var session = new TraceSession();
        var frame = session.Frames.BeginFrame(FrameType.Game, 0.0);
        session.Frames.EndFrame(FrameType.Game, depth * 2.0);
        session.Threads.AddGameThread(1);

        var timerId = session.Timers.AddTimer("Nested", null, 0, TimerType.CpuScope);
        var timeline = session.CpuTimelines.GetOrCreateTimeline(1);

        for (var index = 0; index < depth; index++)
        {
            timeline.AddEvent(new TimelineEvent(index, true, TimerRef.ForTimer(timerId)));
        }

        for (var index = 0; index < depth; index++)
        {
            timeline.AddEvent(new TimelineEvent(depth + index + 1, false, TimerRef.ForTimer(0)));
        }

        var builder = new CpuCallTreeBuilder();
        var threads = builder.BuildForFrame(session, frame);

        var root = Assert.Single(Assert.Single(threads).Roots);
        Assert.Equal("Nested", root.Name);
        Assert.True(root.Children.Count > 0);
    }
}
