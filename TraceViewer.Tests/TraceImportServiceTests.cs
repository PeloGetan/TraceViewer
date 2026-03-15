using TraceViewer.Import;
using TraceViewer.Query;
using Xunit.Abstractions;

namespace TraceViewer.Tests;

public sealed class TraceImportServiceTests
{
    private readonly ITestOutputHelper _output;

    public TraceImportServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Import_LocalSampleTrace_ProducesSessionFromRealFile()
    {
        var tracePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "20260315_164055.utrace"));
        if (!File.Exists(tracePath))
        {
            return;
        }

        var service = new TraceImportService();
        var result = service.Import(tracePath);

        Assert.NotNull(result.ReadResult.FileHeader);
        Assert.NotEmpty(result.ReadResult.Packets);
        Assert.NotEmpty(result.ReadResult.Events);
        Assert.True(result.Session.DurationSeconds > 0);
        Assert.NotEmpty(result.Session.Threads.GetOrderedThreads());
        var gameFrames = result.Session.Frames.GetSeries(SessionModel.FrameType.Game).Frames.Where(frame => double.IsFinite(frame.EndTime)).ToArray();
        Assert.True(gameFrames.Length > 0 || result.Session.CpuTimelines.Timelines.Count > 0);
        if (gameFrames.Length > 0)
        {
            var lastFrameEnd = gameFrames[^1].EndTime;
            Assert.InRange(result.Session.DurationSeconds, gameFrames[0].StartTime, lastFrameEnd + 3600.0);
        }
    }

    [Fact]
    public void Import_LocalSampleTrace_ReportsDecoderCoverage()
    {
        var tracePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "20260315_164055.utrace"));
        if (!File.Exists(tracePath))
        {
            return;
        }

        var service = new TraceImportService();
        var result = service.Import(tracePath);

        _output.WriteLine($"Packets: {result.ReadResult.Packets.Count}");
        _output.WriteLine($"Events: {result.ReadResult.Events.Count}");
        _output.WriteLine($"Duration: {result.Session.DurationSeconds:F6}s");
        _output.WriteLine($"GameFrames: {result.Session.Frames.GetSeries(SessionModel.FrameType.Game).Frames.Count}");
        _output.WriteLine($"RenderFrames: {result.Session.Frames.GetSeries(SessionModel.FrameType.Rendering).Frames.Count}");
        _output.WriteLine($"Threads: {result.Session.Threads.GetOrderedThreads().Count}");
        _output.WriteLine($"CpuTimelines: {result.Session.CpuTimelines.Timelines.Count}");

        foreach (var group in result.ReadResult.Events
                     .GroupBy(evt => $"{evt.Descriptor.Logger}.{evt.Descriptor.EventName}")
                     .OrderByDescending(group => group.Count())
                     .Take(20))
        {
            _output.WriteLine($"{group.Key} = {group.Count()}");
        }

        Assert.NotEmpty(result.ReadResult.Events);
    }

    [Fact]
    public void Import_LocalSampleTrace_BuildsQueryableFrameAndThreadView()
    {
        var tracePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "20260315_164055.utrace"));
        if (!File.Exists(tracePath))
        {
            return;
        }

        var service = new TraceImportService();
        var result = service.Import(tracePath);

        var gameSeries = result.Session.Frames.GetSeries(SessionModel.FrameType.Game);
        var completedFrames = gameSeries.Frames.Where(frame => double.IsFinite(frame.EndTime)).ToArray();
        Assert.NotEmpty(completedFrames);

        var frameTimelineQuery = new FrameTimelineQuery();
        var bars = frameTimelineQuery.GetBars(
            gameSeries,
            completedFrames[0].StartTime,
            completedFrames[Math.Min(completedFrames.Length - 1, 119)].EndTime);
        Assert.NotEmpty(bars);

        var callTreeBuilder = new CpuCallTreeBuilder();
        var threadActivityQuery = new ThreadActivityQuery(callTreeBuilder);
        var detailsQuery = new FunctionDetailsQuery();

        ThreadFrameView[]? activeThreads = null;
        SessionModel.FrameInfo? selectedFrame = null;

        var candidateIndices = new SortedSet<int>();
        for (var index = 0; index < Math.Min(completedFrames.Length, 60); index += 5)
        {
            candidateIndices.Add(index);
        }

        candidateIndices.Add(completedFrames.Length / 4);
        candidateIndices.Add(completedFrames.Length / 2);
        candidateIndices.Add(Math.Max(0, completedFrames.Length - 1));

        var bestActiveThreadCount = 0;

        foreach (var candidateIndex in candidateIndices)
        {
            var frame = completedFrames[candidateIndex];
            var threads = threadActivityQuery.GetActiveThreads(result.Session, frame).ToArray();
            bestActiveThreadCount = Math.Max(bestActiveThreadCount, threads.Length);
            if (threads.Length == 0)
            {
                continue;
            }

            if (activeThreads is null || threads.Length > activeThreads.Length)
            {
                activeThreads = threads;
                selectedFrame = frame;
            }
        }

        Assert.NotNull(selectedFrame);
        Assert.NotNull(activeThreads);
        Assert.NotEmpty(activeThreads!);
        Assert.NotEmpty(activeThreads![0].Roots);
        Assert.True(bestActiveThreadCount > 1, "Sample trace should expose more than one active CPU thread for at least one candidate frame.");
        Assert.Contains(activeThreads!, thread => !string.Equals(thread.Thread.Name, "UnnamedThread", StringComparison.Ordinal));

        var details = detailsQuery.Build(activeThreads[0].Thread, activeThreads[0].Roots[0]);
        Assert.False(string.IsNullOrWhiteSpace(details.Name));
        Assert.False(string.IsNullOrWhiteSpace(details.Thread));
        Assert.True(details.InclusiveMilliseconds >= 0);

        _output.WriteLine($"Queryable frame: {selectedFrame!.Index}");
        _output.WriteLine($"Active threads: {activeThreads.Length}");
        _output.WriteLine($"First node: {details.Name} on {details.Thread}");
    }
}
