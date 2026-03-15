using TraceViewer.Analysis;
using TraceViewer.SessionModel;
using TraceViewer.TraceFormat;

namespace TraceViewer.Tests;

public sealed class CpuProfilerModuleTests
{
    [Fact]
    public void Execute_DecodesEventSpecAndBatchIntoCpuTimeline()
    {
        var batch = EncodeEventBatchV1(
            (10UL, true, 100U),
            (16UL, false, null));

        var events = new TraceEvent[]
        {
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventSpec",
                0.0,
                fields: new Dictionary<string, object?>
                {
                    ["Id"] = 100u,
                    ["Name"] = "Tick",
                    ["File"] = "GameThread.cpp",
                    ["Line"] = 42u,
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventBatch",
                0.0,
                threadId: 7,
                fields: new Dictionary<string, object?>
                {
                    ["BaseCycle"] = 0UL,
                    ["SecondsPerCycle"] = 0.001d,
                    ["Data"] = batch,
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EndThread",
                0.0,
                threadId: 7,
                fields: new Dictionary<string, object?>
                {
                    ["Cycle"] = 16UL,
                    ["SecondsPerCycle"] = 0.001d,
                }),
        };

        var pipeline = new AnalysisPipeline([new CpuProfilerModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var timer = session.Timers.GetTimer(1);
        Assert.NotNull(timer);
        Assert.Equal("Tick", timer!.Name);
        Assert.Equal("GameThread.cpp", timer.SourceFile);
        Assert.Equal<uint>(42, timer.SourceLine);

        var timeline = session.CpuTimelines.TryGetTimeline(7);
        Assert.NotNull(timeline);
        Assert.Collection(
            timeline!.Events,
            evt =>
            {
                Assert.True(evt.IsBegin);
                Assert.Equal(0.010d, evt.Timestamp, 3);
                Assert.Equal(TimerRef.ForTimer(1), evt.TimerRef);
            },
            evt =>
            {
                Assert.False(evt.IsBegin);
                Assert.Equal(0.016d, evt.Timestamp, 3);
            });

        var thread = session.Threads.TryGetThread(7);
        Assert.NotNull(thread);
    }

    [Fact]
    public void Execute_DecodesEventBatchV2IntoCpuTimeline()
    {
        var batch = EncodeEventBatchV2(
            (4UL, true, 300U),
            (9UL, false, null));

        var events = new TraceEvent[]
        {
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventSpec",
                0.0,
                fields: new Dictionary<string, object?>
                {
                    ["Id"] = 300u,
                    ["Name"] = "V2Scope",
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventBatchV2",
                0.0,
                threadId: 11,
                fields: new Dictionary<string, object?>
                {
                    ["BaseCycle"] = 0UL,
                    ["SecondsPerCycle"] = 0.002d,
                    ["Data"] = batch,
                }),
        };

        var pipeline = new AnalysisPipeline([new CpuProfilerModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var timeline = session.CpuTimelines.TryGetTimeline(11);
        Assert.NotNull(timeline);
        Assert.Collection(
            timeline!.Events,
            evt =>
            {
                Assert.True(evt.IsBegin);
                Assert.Equal(0.008d, evt.Timestamp, 3);
                Assert.Equal(TimerRef.ForTimer(1), evt.TimerRef);
            },
            evt =>
            {
                Assert.False(evt.IsBegin);
                Assert.Equal(0.018d, evt.Timestamp, 3);
            });
    }

    [Fact]
    public void Execute_UpdatesUnknownTimerWhenEventSpecArrivesAfterBatch()
    {
        var batch = EncodeEventBatchV1(
            (12UL, true, 200U),
            (20UL, false, null));

        var events = new TraceEvent[]
        {
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventBatch",
                0.0,
                threadId: 3,
                fields: new Dictionary<string, object?>
                {
                    ["BaseCycle"] = 0UL,
                    ["SecondsPerCycle"] = 0.001d,
                    ["Data"] = batch,
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventSpec",
                0.0,
                fields: new Dictionary<string, object?>
                {
                    ["Id"] = 200u,
                    ["Name"] = "LateResolved",
                }),
        };

        var pipeline = new AnalysisPipeline([new CpuProfilerModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var timer = session.Timers.GetTimer(1);
        Assert.NotNull(timer);
        Assert.Equal("LateResolved", timer!.Name);

        var timeline = session.CpuTimelines.TryGetTimeline(3);
        Assert.NotNull(timeline);
        Assert.Equal(TimerRef.ForTimer(1), timeline!.Events[0].TimerRef);
    }

    [Fact]
    public void Execute_ResolvesMetadataPlaceholderForEventBatchV3()
    {
        var batch = EncodeEventBatchV3(
            (5UL, true, 7U, isMetadata: true),
            (9UL, false, null, isMetadata: false));

        var events = new TraceEvent[]
        {
            TraceEventFactory.Create(
                "CpuProfiler",
                "MetadataSpec",
                0.0,
                fields: new Dictionary<string, object?>
                {
                    ["Id"] = 401u,
                    ["Name"] = "Task",
                    ["NameFormat"] = "Task %s",
                    ["FieldNames"] = new[] { "Name" },
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventBatchV3",
                0.0,
                threadId: 12,
                fields: new Dictionary<string, object?>
                {
                    ["BaseCycle"] = 0UL,
                    ["SecondsPerCycle"] = 0.001d,
                    ["Data"] = batch,
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "Metadata",
                0.0,
                fields: new Dictionary<string, object?>
                {
                    ["Id"] = 7u,
                    ["SpecId"] = 401u,
                    ["Metadata"] = new byte[] { 1, 2, 3, 4 },
                }),
        };

        var pipeline = new AnalysisPipeline([new CpuProfilerModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var metadataInstance = session.Metadata.GetInstance(0);
        Assert.NotNull(metadataInstance);

        var resolvedTimer = session.ResolveTimerDefinition(TimerRef.ForMetadata(0));
        Assert.NotNull(resolvedTimer);
        Assert.Equal("Task", resolvedTimer!.Name);
        Assert.Equal(0, resolvedTimer.MetadataSpecId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, metadataInstance!.Payload.ToArray());

        var timeline = session.CpuTimelines.TryGetTimeline(12);
        Assert.NotNull(timeline);
        Assert.Equal(TimerRef.ForMetadata(0), timeline!.Events[0].TimerRef);
    }

    [Fact]
    public void Execute_InterleavesCpuLoggerScopesIntoBatchTimeline()
    {
        var beginBatch = EncodeEventBatchV1((10UL, true, 100U));
        var endBatch = EncodeEventBatchV1((20UL, false, null));

        var events = new TraceEvent[]
        {
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventSpec",
                0.0,
                fields: new Dictionary<string, object?>
                {
                    ["Id"] = 100u,
                    ["Name"] = "FrameWork",
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventBatch",
                0.0,
                threadId: 21,
                fields: new Dictionary<string, object?>
                {
                    ["BaseCycle"] = 0UL,
                    ["SecondsPerCycle"] = 0.001d,
                    ["Data"] = beginBatch,
                }),
            TraceEventFactory.Create(
                "Cpu",
                "WaitScope",
                0.012d,
                threadId: 21,
                cycle: 12UL,
                scopePhase: TraceEventScopePhase.Enter,
                fields: new Dictionary<string, object?>
                {
                    ["TypeId"] = 33u,
                    ["Payload"] = new byte[] { 9, 8, 7 },
                }),
            TraceEventFactory.Create(
                "Cpu",
                "WaitScope",
                0.018d,
                threadId: 21,
                cycle: 18UL,
                scopePhase: TraceEventScopePhase.Leave),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EventBatch",
                0.0,
                threadId: 21,
                fields: new Dictionary<string, object?>
                {
                    ["BaseCycle"] = 0UL,
                    ["SecondsPerCycle"] = 0.001d,
                    ["Data"] = endBatch,
                }),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EndThread",
                0.0,
                threadId: 21,
                fields: new Dictionary<string, object?>
                {
                    ["Cycle"] = 20UL,
                    ["SecondsPerCycle"] = 0.001d,
                }),
        };

        var pipeline = new AnalysisPipeline([new CpuProfilerModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var timeline = session.CpuTimelines.TryGetTimeline(21);
        Assert.NotNull(timeline);
        Assert.Collection(
            timeline!.Events,
            evt =>
            {
                Assert.True(evt.IsBegin);
                Assert.Equal(0.010d, evt.Timestamp, 3);
                Assert.Equal(TimerRef.ForTimer(1), evt.TimerRef);
            },
            evt =>
            {
                Assert.True(evt.IsBegin);
                Assert.Equal(0.012d, evt.Timestamp, 3);
                Assert.Equal(TimerRef.ForMetadata(0), evt.TimerRef);
            },
            evt =>
            {
                Assert.False(evt.IsBegin);
                Assert.Equal(0.018d, evt.Timestamp, 3);
            },
            evt =>
            {
                Assert.False(evt.IsBegin);
                Assert.Equal(0.020d, evt.Timestamp, 3);
            });

        var metadataInstance = session.Metadata.GetInstance(0);
        Assert.NotNull(metadataInstance);
        Assert.Equal(new byte[] { 9, 8, 7 }, metadataInstance!.Payload.ToArray());

        var timer = session.ResolveTimerDefinition(TimerRef.ForMetadata(0));
        Assert.NotNull(timer);
        Assert.Equal("WaitScope", timer!.Name);
    }

    [Fact]
    public void Execute_FlushesPendingCpuLoggerScopesOnEndThread()
    {
        var events = new TraceEvent[]
        {
            TraceEventFactory.Create(
                "Cpu",
                "StandaloneScope",
                0.005d,
                threadId: 31,
                cycle: 5UL,
                scopePhase: TraceEventScopePhase.Enter,
                fields: new Dictionary<string, object?>
                {
                    ["TypeId"] = 77u,
                    ["Payload"] = new byte[] { 1, 1, 2 },
                }),
            TraceEventFactory.Create(
                "Cpu",
                "StandaloneScope",
                0.006d,
                threadId: 31,
                cycle: 6UL,
                scopePhase: TraceEventScopePhase.Leave),
            TraceEventFactory.Create(
                "CpuProfiler",
                "EndThread",
                0.0,
                threadId: 31,
                fields: new Dictionary<string, object?>
                {
                    ["Cycle"] = 7UL,
                    ["SecondsPerCycle"] = 0.001d,
                }),
        };

        var pipeline = new AnalysisPipeline([new CpuProfilerModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var timeline = session.CpuTimelines.TryGetTimeline(31);
        Assert.NotNull(timeline);
        Assert.Collection(
            timeline!.Events,
            evt =>
            {
                Assert.True(evt.IsBegin);
                Assert.Equal(0.005d, evt.Timestamp, 3);
                Assert.Equal(TimerRef.ForMetadata(0), evt.TimerRef);
            },
            evt =>
            {
                Assert.False(evt.IsBegin);
                Assert.Equal(0.006d, evt.Timestamp, 3);
            });
    }

    private static byte[] EncodeEventBatchV1(params (ulong cycle, bool isBegin, uint? specId)[] events)
    {
        var buffer = new List<byte>();
        ulong lastCycle = 0;

        foreach (var (cycle, isBegin, specId) in events)
        {
            var delta = cycle >= lastCycle ? cycle - lastCycle : cycle;
            var encodedCycle = (delta << 1) | (isBegin ? 1UL : 0UL);
            Write7Bit(buffer, encodedCycle);
            if (isBegin)
            {
                Write7Bit(buffer, specId ?? 0u);
            }

            lastCycle = cycle;
        }

        return buffer.ToArray();
    }

    private static byte[] EncodeEventBatchV2(params (ulong cycle, bool isBegin, uint? specId)[] events)
    {
        var buffer = new List<byte>();

        foreach (var (cycle, isBegin, specId) in events)
        {
            var encodedCycle = (cycle << 2) | (isBegin ? 1UL : 0UL);
            Write7Bit(buffer, encodedCycle);
            if (isBegin)
            {
                Write7Bit(buffer, specId ?? 0u);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] EncodeEventBatchV3(params (ulong cycle, bool isBegin, uint? id, bool isMetadata)[] events)
    {
        var buffer = new List<byte>();

        foreach (var (cycle, isBegin, id, isMetadata) in events)
        {
            var encodedCycle = (cycle << 2) | (isBegin ? 1UL : 0UL);
            Write7Bit(buffer, encodedCycle);
            if (isBegin)
            {
                var rawId = isMetadata ? ((id ?? 0u) << 1) | 1u : ((id ?? 0u) << 1);
                Write7Bit(buffer, rawId);
            }
        }

        return buffer.ToArray();
    }

    private static void Write7Bit(List<byte> buffer, ulong value)
    {
        do
        {
            var next = (byte)(value & 0x7FUL);
            value >>= 7;
            if (value != 0)
            {
                next |= 0x80;
            }

            buffer.Add(next);
        }
        while (value != 0);
    }
}
