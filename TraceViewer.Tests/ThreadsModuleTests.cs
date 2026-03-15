using TraceViewer.Analysis;
using TraceViewer.SessionModel;
using TraceViewer.TraceFormat;

namespace TraceViewer.Tests;

public sealed class ThreadsModuleTests
{
    [Fact]
    public void Execute_BuildsThreadsAndPropagatesGroupScopeToCreatedThread()
    {
        var events = new TraceEvent[]
        {
            TraceEventFactory.Create("Misc", "RegisterGameThread", 0.1, threadId: 10),
            TraceEventFactory.Create(
                "Misc",
                "BeginThreadGroupScope",
                0.2,
                fields: new Dictionary<string, object?>
                {
                    ["CurrentThreadId"] = 10u,
                    ["GroupName"] = "TaskGraphHigh",
                }),
            TraceEventFactory.Create(
                "Misc",
                "CreateThread",
                0.3,
                fields: new Dictionary<string, object?>
                {
                    ["CreatedThreadId"] = 20u,
                    ["CurrentThreadId"] = 10u,
                    ["Name"] = "Worker20",
                    ["Priority"] = (int)ProfilerThreadPriority.Normal,
                }),
            TraceEventFactory.Create(
                "Misc",
                "ThreadInfo",
                0.4,
                threadId: 30,
                fields: new Dictionary<string, object?>
                {
                    ["Name"] = "RHIThread",
                    ["Priority"] = (int)ProfilerThreadPriority.Normal,
                }),
        };

        var pipeline = new AnalysisPipeline([new ThreadsModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var ordered = session.Threads.GetOrderedThreads();
        Assert.Equal(3, ordered.Count);
        Assert.Equal("GameThread", ordered[0].Name);
        Assert.Equal("RHIThread", ordered[1].Name);
        Assert.Equal("Render", ordered[1].GroupName);
        Assert.Equal("Worker20", ordered[2].Name);
        Assert.Equal("TaskGraphHigh", ordered[2].GroupName);
    }

    [Fact]
    public void Execute_DecodesAttachmentBackedThreadNamesAndGroups()
    {
        var workerName = System.Text.Encoding.Unicode.GetBytes("WorkerUtf16\0");
        var threadGroup = System.Text.Encoding.UTF8.GetBytes("IOThreadPool\0");

        var events = new TraceEvent[]
        {
            TraceEventFactory.Create(
                "Misc",
                "BeginThreadGroupScope",
                0.1,
                fields: new Dictionary<string, object?>
                {
                    ["CurrentThreadId"] = 10u,
                },
                attachment: threadGroup),
            TraceEventFactory.Create(
                "Misc",
                "CreateThread",
                0.2,
                fields: new Dictionary<string, object?>
                {
                    ["CreatedThreadId"] = 11u,
                    ["CurrentThreadId"] = 10u,
                    ["Priority"] = (int)ProfilerThreadPriority.Normal,
                },
                attachment: workerName),
            TraceEventFactory.Create(
                "$Trace",
                "ThreadInfo",
                0.3,
                fields: new Dictionary<string, object?>
                {
                    ["ThreadId"] = 11u,
                    ["SortHint"] = (int)ProfilerThreadPriority.Normal,
                    ["Name"] = "WorkerUtf16",
                }),
            TraceEventFactory.Create(
                "Misc",
                "SetThreadGroup",
                0.4,
                fields: new Dictionary<string, object?>
                {
                    ["ThreadId"] = 11u,
                },
                attachment: System.Text.Encoding.UTF8.GetBytes("LargeThreadPool\0")),
        };

        var pipeline = new AnalysisPipeline([new ThreadsModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var thread = session.Threads.TryGetThread(11);
        Assert.NotNull(thread);
        Assert.Equal("WorkerUtf16", thread!.Name);
        Assert.Equal("LargeThreadPool", thread.GroupName);
    }

    [Fact]
    public void Complete_InferMainThreadAsGameThreadWhenTraceDoesNotNameIt()
    {
        var events = new TraceEvent[]
        {
            TraceEventFactory.Create("CpuProfiler", "EndThread", 0.1, threadId: 2),
        };

        var pipeline = new AnalysisPipeline([new ThreadsModule(), new CpuProfilerModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var thread = session.Threads.TryGetThread(2);
        Assert.NotNull(thread);
        Assert.Equal("GameThread", thread!.Name);
        Assert.Equal(ProfilerThreadPriority.GameThread, thread.Priority);
    }
}
