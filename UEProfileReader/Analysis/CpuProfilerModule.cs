using UEProfileReader.SessionModel;
using UEProfileReader.TraceFormat;

namespace UEProfileReader.Analysis;

public sealed class CpuProfilerModule : ITraceAnalysisModule
{
    private const double MaxBatchLeadSeconds = 3600.0;

    private readonly record struct PendingEvent(ulong Cycle, double Time, bool IsBegin, TimerRef TimerRef);

    private sealed class ThreadState
    {
        public required uint ThreadId { get; init; }

        public required CpuThreadTimeline Timeline { get; init; }

        public ulong LastCycle { get; set; }

        public double LastPendingEventTime { get; set; }

        public bool ShouldIgnorePendingEvents { get; set; }

        public List<PendingEvent> PendingEvents { get; } = [];

        public Stack<TimerRef> ScopeStack { get; } = new();
    }

    private readonly Dictionary<uint, int> _specIdToTimerId = [];
    private readonly Dictionary<string, int> _scopeNameToTimerId = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, ThreadState> _threadStates = [];
    private readonly Dictionary<uint, int> _metadataIdToInstanceId = [];

    private int? _metadataUnknownTimerId;

    public string Name => "CpuProfiler";

    public void Initialize(IAnalysisContext context)
    {
        _specIdToTimerId.Clear();
        _scopeNameToTimerId.Clear();
        _threadStates.Clear();
        _metadataIdToInstanceId.Clear();
        _metadataUnknownTimerId = null;
    }

    public bool CanHandle(in TraceEvent traceEvent)
    {
        if (traceEvent.Descriptor.Logger == "CpuProfiler")
        {
            return traceEvent.Descriptor.EventName is
                "EventSpec" or
                "EventBatch" or
                "EventBatchV2" or
                "EventBatchV3" or
                "MetadataSpec" or
                "Metadata" or
                "EndThread";
        }

        return traceEvent.Descriptor.Logger == "Cpu" &&
               traceEvent.Descriptor.ScopePhase is TraceEventScopePhase.Enter or TraceEventScopePhase.Leave;
    }

    public void Process(in TraceEvent traceEvent, IAnalysisContext context)
    {
        if (traceEvent.Descriptor.Logger == "Cpu")
        {
            ProcessCpuScope(traceEvent, context.Session);
            return;
        }

        switch (traceEvent.Descriptor.EventName)
        {
            case "EventSpec":
                OnEventSpec(traceEvent, context.Session);
                break;

            case "EventBatch":
                OnEventBatch(traceEvent, context.Session, BatchEncoding.V1);
                break;

            case "EventBatchV2":
                OnEventBatch(traceEvent, context.Session, BatchEncoding.V2);
                break;

            case "EventBatchV3":
                OnEventBatch(traceEvent, context.Session, BatchEncoding.V3);
                break;

            case "MetadataSpec":
                OnMetadataSpec(traceEvent, context.Session);
                break;

            case "Metadata":
                OnMetadata(traceEvent, context.Session);
                break;

            case "EndThread":
                OnEndThread(traceEvent, context.Session);
                break;
        }
    }

    public void Complete(IAnalysisContext context)
    {
        foreach (var threadState in _threadStates.Values)
        {
            DispatchRemainingPendingEvents(threadState);
            if (threadState.LastCycle != ulong.MaxValue && threadState.ScopeStack.Count > 0)
            {
                EndOpenEvents(threadState, context.Session.DurationSeconds);
            }
        }
    }

    private void OnEventSpec(TraceEvent traceEvent, TraceSession session)
    {
        var specId = GetUInt32(traceEvent, "Id");
        var timerName = GetString(traceEvent, "Name");
        if (string.IsNullOrWhiteSpace(timerName))
        {
            timerName = $"<noname {specId}>";
        }

        var sourceFile = GetString(traceEvent, "File");
        var sourceLine = GetUInt32(traceEvent, "Line");

        DefineMergedTimer(specId, timerName, sourceFile, sourceLine, session);
    }

    private void OnMetadataSpec(TraceEvent traceEvent, TraceSession session)
    {
        var specId = GetUInt32(traceEvent, "Id");
        var name = GetString(traceEvent, "Name");
        var format = GetString(traceEvent, "NameFormat");
        var fieldNames = GetStringList(traceEvent, "FieldNames");

        if (string.IsNullOrWhiteSpace(name))
        {
            name = InferMetadataName(format) ?? $"<metadata {specId}>";
        }

        var timerId = DefineUniqueTimer(specId, name, null, 0, session);
        var metadataSpecId = session.Metadata.AddSpec(format, fieldNames);
        session.Timers.UpdateTimer(timerId, metadataSpecId: metadataSpecId);
    }

    private void OnMetadata(TraceEvent traceEvent, TraceSession session)
    {
        var metadataId = GetUInt32(traceEvent, "Id");
        var specId = GetUInt32(traceEvent, "SpecId");
        var payload = GetBytes(traceEvent, "Metadata").ToArray();
        var timerId = GetOrAddTimer(specId, session);

        if (_metadataIdToInstanceId.TryGetValue(metadataId, out var instanceId))
        {
            session.Metadata.UpdateInstance(instanceId, timerId, payload);
        }
        else
        {
            instanceId = session.Metadata.AddInstance(timerId, payload);
            _metadataIdToInstanceId.Add(metadataId, instanceId);
        }
    }

    private void OnEventBatch(TraceEvent traceEvent, TraceSession session, BatchEncoding encoding)
    {
        var baseCycle = GetUInt64(traceEvent, "BaseCycle", traceEvent.Timestamp.Cycle ?? 0UL);
        var secondsPerCycle = GetDouble(traceEvent, "SecondsPerCycle", traceEvent.Timestamp.SecondsPerCycle ?? 0.0);
        var batchContextTime = traceEvent.Timestamp.Seconds;
        var data = GetBytes(traceEvent, "Data");
        if (data.Length == 0)
        {
            return;
        }

        var threadState = GetOrAddThreadState(traceEvent.ThreadId, session);
        ProcessBuffer(data, baseCycle, secondsPerCycle, batchContextTime, threadState, session, encoding);
    }

    private void OnEndThread(TraceEvent traceEvent, TraceSession session)
    {
        var threadState = GetOrAddThreadState(traceEvent.ThreadId, session);
        if (threadState.LastCycle == ulong.MaxValue)
        {
            return;
        }

        var cycle = GetUInt64(traceEvent, "Cycle", threadState.LastCycle);
        var secondsPerCycle = GetDouble(traceEvent, "SecondsPerCycle");
        var timestamp = NormalizeTimelineTimestamp(threadState, cycle * secondsPerCycle);

        session.UpdateDuration(timestamp);
        DispatchRemainingPendingEvents(threadState);
        EndOpenEvents(threadState, timestamp);
        threadState.LastCycle = ulong.MaxValue;
    }

    private void ProcessCpuScope(TraceEvent traceEvent, TraceSession session)
    {
        switch (traceEvent.Descriptor.ScopePhase)
        {
            case TraceEventScopePhase.Enter:
                OnCpuScopeEnter(traceEvent, session);
                break;

            case TraceEventScopePhase.Leave:
                OnCpuScopeLeave(traceEvent, session);
                break;
        }
    }

    private void ProcessBuffer(
        ReadOnlySpan<byte> data,
        ulong baseCycle,
        double secondsPerCycle,
        double batchContextTime,
        ThreadState threadState,
        TraceSession session,
        BatchEncoding encoding)
    {
        var offset = 0;
        var lastCycle = threadState.LastCycle;

        while (offset < data.Length)
        {
            if (!TryDecode7Bit(data, ref offset, out var decodedCycle))
            {
                break;
            }

            var actualCycle = encoding switch
            {
                BatchEncoding.V1 => decodedCycle >> 1,
                _ => decodedCycle >> 2,
            };

            if (actualCycle < lastCycle)
            {
                actualCycle += lastCycle;
            }

            if (actualCycle < baseCycle)
            {
                actualCycle += baseCycle;
            }

            DispatchPendingEvents(ref lastCycle, actualCycle, threadState, (decodedCycle & 1UL) != 0);
            var actualTime = actualCycle * secondsPerCycle;
            if (batchContextTime > 0 &&
                actualTime > batchContextTime + MaxBatchLeadSeconds)
            {
                break;
            }

            if (encoding != BatchEncoding.V1 && (decodedCycle & 2UL) != 0)
            {
                if (!SkipCoroutineRecord(data, ref offset, decodedCycle))
                {
                    break;
                }

                session.UpdateDuration(actualTime);
                lastCycle = actualCycle;
                continue;
            }

            if ((decodedCycle & 1UL) != 0)
            {
                if (!TryDecode7Bit(data, ref offset, out var rawSpecIdValue))
                {
                    break;
                }

                var rawSpecId = (uint)rawSpecIdValue;
                var timerRef = ResolveTimerRefForBegin(rawSpecId, encoding, session);
                actualTime = NormalizeTimelineTimestamp(threadState, actualTime);
                threadState.ScopeStack.Push(timerRef);
                threadState.Timeline.AddEvent(new TimelineEvent(actualTime, true, timerRef));
            }
            else if (threadState.ScopeStack.Count > 0)
            {
                actualTime = NormalizeTimelineTimestamp(threadState, actualTime);
                threadState.ScopeStack.Pop();
                threadState.Timeline.AddEvent(new TimelineEvent(actualTime, false, TimerRef.ForTimer(0)));
            }

            session.UpdateDuration(actualTime);
            lastCycle = actualCycle;
        }

        TrimDispatchedPendingEvents(threadState);
        threadState.LastCycle = lastCycle;
    }

    private void OnCpuScopeEnter(TraceEvent traceEvent, TraceSession session)
    {
        if (!TryGetEventCycle(traceEvent, out var cycle))
        {
            return;
        }

        var time = traceEvent.Timestamp.Seconds;
        if (time == 0 && cycle == 0)
        {
            return;
        }

        var threadState = GetOrAddThreadState(traceEvent.ThreadId, session);
        if (threadState.ShouldIgnorePendingEvents)
        {
            return;
        }

        var typeId = GetUInt32(traceEvent, "TypeId", GetUInt32(traceEvent, "Id"));
        var specId = ~typeId;
        var scopeName = GetString(traceEvent, "TypeName") ?? traceEvent.Descriptor.EventName;
        var timerId = DefineUniqueTimer(specId, scopeName, null, 0, session);
        var payload = GetPayload(traceEvent);
        var metadataId = session.Metadata.AddInstance(timerId, payload);

        EnqueuePendingEvent(threadState, new PendingEvent(cycle, time, true, TimerRef.ForMetadata(metadataId)));
        session.UpdateDuration(time);
    }

    private void OnCpuScopeLeave(TraceEvent traceEvent, TraceSession session)
    {
        if (!TryGetEventCycle(traceEvent, out var cycle))
        {
            return;
        }

        var time = traceEvent.Timestamp.Seconds;
        if (time == 0 && cycle == 0)
        {
            return;
        }

        var threadState = GetOrAddThreadState(traceEvent.ThreadId, session);
        if (threadState.ShouldIgnorePendingEvents)
        {
            return;
        }

        EnqueuePendingEvent(threadState, new PendingEvent(cycle, time, false, TimerRef.ForTimer(0)));
        session.UpdateDuration(time);
    }

    private TimerRef ResolveTimerRefForBegin(uint rawSpecId, BatchEncoding encoding, TraceSession session)
    {
        if (encoding != BatchEncoding.V3)
        {
            var timerId = GetOrAddTimer(rawSpecId, session);
            return TimerRef.ForTimer(timerId);
        }

        if ((rawSpecId & 1u) != 0)
        {
            var metadataId = rawSpecId >> 1;
            var metadataInstanceId = GetOrCreateMetadataInstance(metadataId, session);
            return TimerRef.ForMetadata(metadataInstanceId);
        }

        var specId = rawSpecId >> 1;
        var timer = GetOrAddTimer(specId, session);
        return TimerRef.ForTimer(timer);
    }

    private int GetOrCreateMetadataInstance(uint metadataId, TraceSession session)
    {
        if (_metadataIdToInstanceId.TryGetValue(metadataId, out var instanceId))
        {
            return instanceId;
        }

        var unknownTimerId = EnsureMetadataUnknownTimer(session);
        instanceId = session.Metadata.AddInstance(unknownTimerId, ReadOnlyMemory<byte>.Empty);
        _metadataIdToInstanceId.Add(metadataId, instanceId);
        return instanceId;
    }

    private int EnsureMetadataUnknownTimer(TraceSession session)
    {
        if (_metadataUnknownTimerId.HasValue)
        {
            return _metadataUnknownTimerId.Value;
        }

        var timerId = session.Timers.AddTimer("<unknown metadata>", null, 0, TimerType.CpuScope);
        _metadataUnknownTimerId = timerId;
        return timerId;
    }

    private void EnqueuePendingEvent(ThreadState threadState, PendingEvent pendingEvent)
    {
        if (pendingEvent.Time < threadState.LastPendingEventTime)
        {
            threadState.ShouldIgnorePendingEvents = true;
            threadState.PendingEvents.Clear();
            return;
        }

        threadState.LastPendingEventTime = pendingEvent.Time;
        threadState.PendingEvents.Add(pendingEvent);
    }

    private void DispatchPendingEvents(ref ulong lastCycle, ulong currentCycle, ThreadState threadState, bool isBeginEvent)
    {
        if (threadState.ShouldIgnorePendingEvents)
        {
            return;
        }

        var pendingEvents = threadState.PendingEvents;
        var dispatchCount = 0;

        while (dispatchCount < pendingEvents.Count)
        {
            var pendingEvent = pendingEvents[dispatchCount];
            if (pendingEvent.Cycle > currentCycle ||
                (pendingEvent.Cycle == currentCycle && !isBeginEvent))
            {
                break;
            }

            if (pendingEvent.Cycle < lastCycle)
            {
                threadState.ShouldIgnorePendingEvents = true;
                threadState.PendingEvents.Clear();
                return;
            }

            lastCycle = pendingEvent.Cycle;
            if (pendingEvent.IsBegin)
            {
                var normalizedTime = NormalizeTimelineTimestamp(threadState, pendingEvent.Time);
                threadState.ScopeStack.Push(pendingEvent.TimerRef);
                threadState.Timeline.AddEvent(new TimelineEvent(normalizedTime, true, pendingEvent.TimerRef));
            }
            else if (threadState.ScopeStack.Count > 0)
            {
                var normalizedTime = NormalizeTimelineTimestamp(threadState, pendingEvent.Time);
                threadState.ScopeStack.Pop();
                threadState.Timeline.AddEvent(new TimelineEvent(normalizedTime, false, TimerRef.ForTimer(0)));
            }

            dispatchCount++;
        }

        if (dispatchCount > 0)
        {
            pendingEvents.RemoveRange(0, dispatchCount);
        }

        threadState.LastCycle = lastCycle;
    }

    private void DispatchRemainingPendingEvents(ThreadState threadState)
    {
        if (threadState.PendingEvents.Count == 0)
        {
            return;
        }

        var lastCycle = threadState.LastCycle;
        DispatchPendingEvents(ref lastCycle, ulong.MaxValue, threadState, true);
        threadState.LastCycle = lastCycle;
    }

    private static void TrimDispatchedPendingEvents(ThreadState threadState)
    {
        if (threadState.ShouldIgnorePendingEvents)
        {
            threadState.PendingEvents.Clear();
        }
    }

    private static bool SkipCoroutineRecord(ReadOnlySpan<byte> data, ref int offset, ulong decodedCycle)
    {
        if ((decodedCycle & 1UL) != 0)
        {
            return TryDecode7Bit(data, ref offset, out _) &&
                   TryDecode7Bit(data, ref offset, out _);
        }

        return TryDecode7Bit(data, ref offset, out _);
    }

    private void EndOpenEvents(ThreadState threadState, double timestamp)
    {
        while (threadState.ScopeStack.Count > 0)
        {
            var normalizedTimestamp = NormalizeTimelineTimestamp(threadState, timestamp);
            threadState.ScopeStack.Pop();
            threadState.Timeline.AddEvent(new TimelineEvent(normalizedTimestamp, false, TimerRef.ForTimer(0)));
        }
    }

    private static double NormalizeTimelineTimestamp(ThreadState threadState, double timestamp)
    {
        return Math.Max(timestamp, threadState.Timeline.LastTimestamp);
    }

    private ThreadState GetOrAddThreadState(uint threadId, TraceSession session)
    {
        if (_threadStates.TryGetValue(threadId, out var threadState))
        {
            return threadState;
        }

        session.Threads.GetOrCreateThread(threadId);
        threadState = new ThreadState
        {
            ThreadId = threadId,
            Timeline = session.CpuTimelines.GetOrCreateTimeline(threadId),
        };

        _threadStates.Add(threadId, threadState);
        return threadState;
    }

    private int GetOrAddTimer(uint specId, TraceSession session)
    {
        if (_specIdToTimerId.TryGetValue(specId, out var timerId))
        {
            return timerId;
        }

        var unknownTimerName = $"<unknown {specId}>";
        timerId = session.Timers.AddTimer(unknownTimerName, null, 0, TimerType.CpuScope);
        _specIdToTimerId.Add(specId, timerId);
        return timerId;
    }

    private int DefineMergedTimer(uint specId, string timerName, string? sourceFile, uint sourceLine, TraceSession session)
    {
        if (_scopeNameToTimerId.TryGetValue(timerName, out var existingTimerId))
        {
            if (_specIdToTimerId.TryGetValue(specId, out var mappedTimerId))
            {
                session.Timers.UpdateTimer(mappedTimerId, timerName, sourceFile, sourceLine);
                return mappedTimerId;
            }

            _specIdToTimerId.Add(specId, existingTimerId);
            return existingTimerId;
        }

        var timerId = DefineUniqueTimer(specId, timerName, sourceFile, sourceLine, session);
        _scopeNameToTimerId[timerName] = timerId;
        return timerId;
    }

    private int DefineUniqueTimer(uint specId, string timerName, string? sourceFile, uint sourceLine, TraceSession session)
    {
        if (_specIdToTimerId.TryGetValue(specId, out var timerId))
        {
            session.Timers.UpdateTimer(timerId, timerName, sourceFile, sourceLine);
            return timerId;
        }

        timerId = session.Timers.AddTimer(timerName, sourceFile, sourceLine, TimerType.CpuScope);
        _specIdToTimerId.Add(specId, timerId);
        return timerId;
    }

    private static ulong Decode7Bit(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        var shift = 0;

        while (offset < data.Length)
        {
            var current = data[offset++];
            value |= ((ulong)(current & 0x7F)) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidOperationException("Unexpected end of 7-bit encoded value.");
    }

    private static bool TryDecode7Bit(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        var snapshot = offset;
        try
        {
            value = Decode7Bit(data, ref snapshot);
            offset = snapshot;
            return true;
        }
        catch (InvalidOperationException)
        {
            value = 0;
            return false;
        }
    }

    private static string? InferMetadataName(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var separators = new[] { '%', ' ', '(', '=' };
        var index = format.IndexOfAny(separators);
        return index > 0 ? format[..index] : format;
    }

    private static string? GetString(TraceEvent traceEvent, string fieldName)
    {
        return traceEvent.Fields.TryGetValue(fieldName, out var value) ? value as string : null;
    }

    private static IReadOnlyList<string> GetStringList(TraceEvent traceEvent, string fieldName)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        return value switch
        {
            string[] array => array,
            List<string> list => list,
            IReadOnlyList<string> readOnlyList => readOnlyList,
            _ => Array.Empty<string>(),
        };
    }

    private static uint GetUInt32(TraceEvent traceEvent, string fieldName, uint fallback = 0)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            uint uintValue => uintValue,
            int intValue => (uint)intValue,
            long longValue => (uint)longValue,
            ulong ulongValue => (uint)ulongValue,
            _ => fallback,
        };
    }

    private static ulong GetUInt64(TraceEvent traceEvent, string fieldName, ulong fallback = 0)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            ulong ulongValue => ulongValue,
            long longValue => (ulong)longValue,
            uint uintValue => uintValue,
            int intValue => (ulong)intValue,
            _ => fallback,
        };
    }

    private static double GetDouble(TraceEvent traceEvent, string fieldName, double fallback = 0.0)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            _ => fallback,
        };
    }

    private static ReadOnlySpan<byte> GetBytes(TraceEvent traceEvent, string fieldName)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return value switch
        {
            byte[] bytes => bytes,
            ReadOnlyMemory<byte> memory => memory.Span,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static ReadOnlyMemory<byte> GetPayload(TraceEvent traceEvent)
    {
        var payload = GetBytes(traceEvent, "Payload");
        if (!payload.IsEmpty)
        {
            return payload.ToArray();
        }

        return traceEvent.Attachment;
    }

    private static bool TryGetEventCycle(TraceEvent traceEvent, out ulong cycle)
    {
        if (traceEvent.Timestamp.Cycle.HasValue)
        {
            cycle = traceEvent.Timestamp.Cycle.Value;
            return true;
        }

        if (traceEvent.Fields.TryGetValue("Cycle", out var value) && value is not null)
        {
            cycle = value switch
            {
                ulong ulongValue => ulongValue,
                long longValue => (ulong)longValue,
                uint uintValue => uintValue,
                int intValue => (ulong)intValue,
                _ => 0UL,
            };

            return true;
        }

        cycle = 0;
        return false;
    }

    private enum BatchEncoding
    {
        V1,
        V2,
        V3,
    }
}
