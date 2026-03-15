using UEProfileReader.Import;
using UEProfileReader.Query;
using UEProfileReader.SessionModel;
using UEProfileReader.TraceFormat;
using UEProfileReader.UI.ViewModels;

namespace UEProfileReader.Tests;

public sealed class ProfilerScreenViewModelTests
{
    [Fact]
    public void ApplyImportResult_SelectsMostInformativeFrameAndPopulatesDetails()
    {
        var session = CreateSession();
        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        viewModel.ApplyImportResult(new TraceImportResult(new TraceReadResult(Array.Empty<TraceEvent>()), session), "sample.utrace");

        Assert.True(viewModel.HasFrameBars);
        Assert.True(viewModel.HasThreadItems);
        Assert.True(viewModel.HasSelectedNode);

        var selectedBar = Assert.Single(viewModel.FrameBars, bar => bar.IsSelected);
        Assert.Equal<ulong>(2UL, selectedBar.Frame.Index);
        Assert.Equal(3, viewModel.ThreadItems.Count);
        Assert.Equal("Physics", viewModel.Details[0].Value);
        Assert.Equal("Worker A", viewModel.Details[1].Value);
        Assert.Equal("500.000", viewModel.Details[2].Value);
    }

    [Fact]
    public void SelectTreeItem_NullClearsDetails()
    {
        var session = CreateSession();
        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        viewModel.ApplyImportResult(new TraceImportResult(new TraceReadResult(Array.Empty<TraceEvent>()), session), "sample.utrace");
        viewModel.SelectTreeItem(null);

        Assert.False(viewModel.HasSelectedNode);
        Assert.Equal("-", viewModel.Details[0].Value);
        Assert.Equal("-", viewModel.Details[1].Value);
    }

    [Fact]
    public void FrameBarWidth_ClampsToSupportedRange()
    {
        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        viewModel.FrameBarWidth = 1.0;
        Assert.Equal(ProfilerScreenViewModel.MinFrameBarWidth, viewModel.FrameBarWidth);

        viewModel.FrameBarWidth = 200.0;
        Assert.Equal(ProfilerScreenViewModel.MaxFrameBarWidth, viewModel.FrameBarWidth);
    }

    [Fact]
    public void ToggleTreeItemSelection_ShiftSelectsRangeAndClipboardExportsSelectedNames()
    {
        var session = CreateFlatSelectionSession();
        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        viewModel.ApplyImportResult(new TraceImportResult(new TraceReadResult(Array.Empty<TraceEvent>()), session), "sample.utrace");

        var firstThreadNode = viewModel.ThreadItems[0].Children[0];
        var thirdThreadNode = viewModel.ThreadItems[2].Children[0];

        viewModel.SetPrimaryTreeItem(firstThreadNode);
        viewModel.ToggleTreeItemSelection(thirdThreadNode, appendSelection: true);

        Assert.True(firstThreadNode.IsSelected);
        Assert.True(viewModel.ThreadItems[1].Children[0].IsSelected);
        Assert.True(thirdThreadNode.IsSelected);
        Assert.Equal(
            $"Physics{Environment.NewLine}Animation{Environment.NewLine}Audio",
            viewModel.BuildSelectedNamesClipboardText());
    }

    [Fact]
    public void SetExpandedRecursively_UpdatesWholeSubtree()
    {
        var session = CreateSession();
        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        viewModel.ApplyImportResult(new TraceImportResult(new TraceReadResult(Array.Empty<TraceEvent>()), session), "sample.utrace");

        var threadNode = viewModel.ThreadItems[0];
        var functionNode = threadNode.Children[0];

        viewModel.SetExpandedRecursively(threadNode, true);
        Assert.True(threadNode.IsExpanded);
        Assert.True(functionNode.IsExpanded);

        viewModel.SetExpandedRecursively(threadNode, false);
        Assert.False(threadNode.IsExpanded);
        Assert.False(functionNode.IsExpanded);
    }

    [Fact]
    public void ExpandToFirstBranching_FollowsSingleChildPath()
    {
        var session = CreateSession();
        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        viewModel.ApplyImportResult(new TraceImportResult(new TraceReadResult(Array.Empty<TraceEvent>()), session), "sample.utrace");

        var threadNode = viewModel.ThreadItems[0];
        var rootNode = threadNode.Children[0];
        var branchNode = rootNode.Children[0];

        threadNode.IsExpanded = false;
        rootNode.IsExpanded = false;
        branchNode.IsExpanded = false;

        viewModel.ExpandToFirstBranching(threadNode);

        Assert.True(threadNode.IsExpanded);
        Assert.True(rootNode.IsExpanded);
        Assert.True(branchNode.IsExpanded);
    }

    [Fact]
    public void SelectTreeItem_ShowsHiddenChainDetailsForCompressedNode()
    {
        var session = CreateSession();
        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        viewModel.ApplyImportResult(new TraceImportResult(new TraceReadResult(Array.Empty<TraceEvent>()), session), "sample.utrace");

        var compressedNode = viewModel.ThreadItems[0].Children[0];

        Assert.True(compressedNode.HasHiddenChain);
        Assert.Equal("+1", compressedNode.HiddenChainBadgeText);

        viewModel.SetPrimaryTreeItem(compressedNode);

        var hiddenChainDetail = Assert.Single(viewModel.Details, detail => detail.Label == "Hidden chain");
        Assert.Equal("PhysicsSubstep", hiddenChainDetail.Value);
    }

    [Fact]
    public async Task LoadTraceAsync_SetsIsLoadingAndRestoresInteractivity()
    {
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var viewModel = new ProfilerScreenViewModel(
            new TraceImportService(new BlockingTraceReader(started, release)),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery());

        var loadTask = viewModel.LoadTraceAsync("blocked.utrace");

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        await Task.Delay(50);
        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.IsInteractive);

        release.Set();
        await loadTask;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.IsInteractive);
        Assert.Contains("no completed Game frames", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static TraceSession CreateFlatSelectionSession()
    {
        var session = new TraceSession();

        session.Frames.BeginFrame(FrameType.Game, 0.0);
        session.Frames.EndFrame(FrameType.Game, 1.0);

        session.Threads.AddOrUpdateThread(11, "Worker A", ProfilerThreadPriority.Normal);
        session.Threads.AddOrUpdateThread(12, "Worker B", ProfilerThreadPriority.Normal);
        session.Threads.AddOrUpdateThread(13, "Worker C", ProfilerThreadPriority.Normal);

        var physics = session.Timers.AddTimer("Physics", null, 0, TimerType.CpuScope);
        var animation = session.Timers.AddTimer("Animation", null, 0, TimerType.CpuScope);
        var audio = session.Timers.AddTimer("Audio", null, 0, TimerType.CpuScope);

        var workerATimeline = session.CpuTimelines.GetOrCreateTimeline(11);
        workerATimeline.AddEvent(new TimelineEvent(0.1, true, TimerRef.ForTimer(physics)));
        workerATimeline.AddEvent(new TimelineEvent(0.4, false, TimerRef.ForTimer(0)));

        var workerBTimeline = session.CpuTimelines.GetOrCreateTimeline(12);
        workerBTimeline.AddEvent(new TimelineEvent(0.2, true, TimerRef.ForTimer(animation)));
        workerBTimeline.AddEvent(new TimelineEvent(0.45, false, TimerRef.ForTimer(0)));

        var workerCTimeline = session.CpuTimelines.GetOrCreateTimeline(13);
        workerCTimeline.AddEvent(new TimelineEvent(0.25, true, TimerRef.ForTimer(audio)));
        workerCTimeline.AddEvent(new TimelineEvent(0.35, false, TimerRef.ForTimer(0)));

        session.UpdateDuration(1.0);
        return session;
    }

    private static TraceSession CreateSession()
    {
        var session = new TraceSession();

        session.Frames.BeginFrame(FrameType.Game, 0.0);
        session.Frames.EndFrame(FrameType.Game, 1.0);
        session.Frames.BeginFrame(FrameType.Game, 1.0);
        session.Frames.EndFrame(FrameType.Game, 2.0);
        session.Frames.BeginFrame(FrameType.Game, 2.0);
        session.Frames.EndFrame(FrameType.Game, 3.0);

        session.Threads.AddOrUpdateThread(10, "GameThread", ProfilerThreadPriority.GameThread);
        session.Threads.AddOrUpdateThread(11, "Worker A", ProfilerThreadPriority.Normal);
        session.Threads.AddOrUpdateThread(12, "Worker B", ProfilerThreadPriority.Normal);
        session.Threads.AddOrUpdateThread(13, "Worker C", ProfilerThreadPriority.Normal);

        var updateWorld = session.Timers.AddTimer("UpdateWorld", null, 0, TimerType.CpuScope);
        var physics = session.Timers.AddTimer("Physics", null, 0, TimerType.CpuScope);
        var physicsSubstep = session.Timers.AddTimer("PhysicsSubstep", null, 0, TimerType.CpuScope);
        var physicsPhase = session.Timers.AddTimer("PhysicsPhase", null, 0, TimerType.CpuScope);
        var physicsLeafA = session.Timers.AddTimer("PhysicsLeafA", null, 0, TimerType.CpuScope);
        var physicsLeafB = session.Timers.AddTimer("PhysicsLeafB", null, 0, TimerType.CpuScope);
        var animation = session.Timers.AddTimer("Animation", null, 0, TimerType.CpuScope);
        var audio = session.Timers.AddTimer("Audio", null, 0, TimerType.CpuScope);

        var gameTimeline = session.CpuTimelines.GetOrCreateTimeline(10);
        gameTimeline.AddEvent(new TimelineEvent(1.1, true, TimerRef.ForTimer(updateWorld)));
        gameTimeline.AddEvent(new TimelineEvent(1.4, false, TimerRef.ForTimer(0)));

        var workerATimeline = session.CpuTimelines.GetOrCreateTimeline(11);
        workerATimeline.AddEvent(new TimelineEvent(2.1, true, TimerRef.ForTimer(physics)));
        workerATimeline.AddEvent(new TimelineEvent(2.2, true, TimerRef.ForTimer(physicsSubstep)));
        workerATimeline.AddEvent(new TimelineEvent(2.23, true, TimerRef.ForTimer(physicsPhase)));
        workerATimeline.AddEvent(new TimelineEvent(2.24, true, TimerRef.ForTimer(physicsLeafA)));
        workerATimeline.AddEvent(new TimelineEvent(2.32, false, TimerRef.ForTimer(0)));
        workerATimeline.AddEvent(new TimelineEvent(2.34, true, TimerRef.ForTimer(physicsLeafB)));
        workerATimeline.AddEvent(new TimelineEvent(2.42, false, TimerRef.ForTimer(0)));
        workerATimeline.AddEvent(new TimelineEvent(2.48, false, TimerRef.ForTimer(0)));
        workerATimeline.AddEvent(new TimelineEvent(2.55, false, TimerRef.ForTimer(0)));
        workerATimeline.AddEvent(new TimelineEvent(2.6, false, TimerRef.ForTimer(0)));

        var workerBTimeline = session.CpuTimelines.GetOrCreateTimeline(12);
        workerBTimeline.AddEvent(new TimelineEvent(2.2, true, TimerRef.ForTimer(animation)));
        workerBTimeline.AddEvent(new TimelineEvent(2.5, false, TimerRef.ForTimer(0)));

        var workerCTimeline = session.CpuTimelines.GetOrCreateTimeline(13);
        workerCTimeline.AddEvent(new TimelineEvent(2.25, true, TimerRef.ForTimer(audio)));
        workerCTimeline.AddEvent(new TimelineEvent(2.4, false, TimerRef.ForTimer(0)));

        session.UpdateDuration(3.0);
        return session;
    }

    private sealed class BlockingTraceReader : ITraceReader
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        public BlockingTraceReader(ManualResetEventSlim started, ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public TraceReadResult Read(string traceFilePath)
        {
            _started.Set();
            _release.Wait(TimeSpan.FromSeconds(2));
            return new TraceReadResult(Array.Empty<TraceEvent>());
        }
    }
}
