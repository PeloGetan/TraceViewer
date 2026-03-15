using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace TraceViewer.TraceFormat;

internal sealed class TraceEventStreamDecoder
{
    private const ushort EventsThreadId = 0;
    private const ushort InternalThreadId = 1;
    private const ushort SyncThreadId = 0x3FFF;

    private readonly IReadOnlyList<TracePacket> _packets;
    private readonly Dictionary<ushort, TraceEventDefinition> _definitions = [];
    private readonly Dictionary<ushort, ThreadState> _threadStates = [];

    private ulong _startCycle;
    private ulong _cycleFrequency;
    private double? _secondsPerCycle;

    public TraceEventStreamDecoder(IReadOnlyList<TracePacket> packets)
    {
        _packets = packets;
    }

    public IReadOnlyList<TraceEvent> Decode()
    {
        var events = new List<TraceEvent>();

        foreach (var packet in _packets)
        {
            if (packet.ThreadId is EventsThreadId or InternalThreadId)
            {
                try
                {
                    DecodeImportantPacket(packet, events);
                }
                catch (InvalidDataException)
                {
                    // Keep packet transport parsing robust while the event decoder is still incomplete.
                }
            }
        }

        foreach (var packet in _packets)
        {
            if (packet.ThreadId is EventsThreadId or InternalThreadId or SyncThreadId)
            {
                continue;
            }

            try
            {
                DecodeThreadPacket(packet, events);
            }
            catch (InvalidDataException)
            {
                // Ignore undecodable packet payloads for now; transport parsing remains useful on its own.
            }
        }

        foreach (var state in _threadStates.Values)
        {
            if (state.PendingEvent is { } pendingEvent)
            {
                events.Add(FinalizeEvent(pendingEvent, state));
                state.PendingEvent = null;
            }
        }

        return events;
    }

    private void DecodeImportantPacket(TracePacket packet, List<TraceEvent> events)
    {
        var data = packet.Payload.Span;
        var offset = 0;
        var state = GetOrAddThreadState(packet.ThreadId);

        while (offset < data.Length)
        {
            EnsureAvailable(data, offset, 4, "important event header");

            var uid = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;

            var size = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
            EnsureAvailable(data, offset, size, "important event payload");

            var payload = data.Slice(offset, size);
            offset += size;

            if (uid == 0)
            {
                RegisterDefinition(payload);
                continue;
            }

            if (!_definitions.TryGetValue(uid, out var definition))
            {
                continue;
            }

            var partial = CreatePartialEvent(definition, packet.ThreadId, TraceEventScopePhase.None, default, payload);
            if (definition.MaybeHasAux)
            {
                var payloadOffset = definition.FixedSize;
                ConsumeAux(ref partial, payload, ref payloadOffset, state, events);
                if (!partial.IsComplete)
                {
                    partial.IsComplete = true;
                    events.Add(FinalizeEvent(partial, state));
                }

                continue;
            }

            events.Add(FinalizeEvent(partial, state));
        }
    }

    private void DecodeThreadPacket(TracePacket packet, List<TraceEvent> events)
    {
        var state = GetOrAddThreadState(packet.ThreadId);
        var data = packet.Payload.Span;
        var offset = 0;

        while (offset < data.Length)
        {
            if (state.PendingEvent is { } pendingEvent)
            {
                ConsumeAux(ref pendingEvent, data, ref offset, state, events);
                state.PendingEvent = pendingEvent.IsComplete ? null : pendingEvent;
                if (state.PendingEvent is not null || offset >= data.Length)
                {
                    continue;
                }
            }

            var marker = data[offset];
            if ((marker & 1) == 0)
            {
                if (marker == 0 && offset + 1 < data.Length && data[offset + 1] == 0)
                {
                    throw new InvalidDataException("Unexpected important event in thread packet.");
                }

                HandleWellKnownMarker(state, data, ref offset, events);
                continue;
            }

            EnsureAvailable(data, offset, 2, "event uid");
            var uid = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) >> 1);
            if (!_definitions.TryGetValue(uid, out var definition))
            {
                throw new InvalidDataException($"Unknown event definition uid {uid}.");
            }

            var headerSize = definition.NoSync ? 2 : 5;
            EnsureAvailable(data, offset, headerSize, "event header");
            offset += headerSize;

            EnsureAvailable(data, offset, definition.FixedSize, "event payload");
            var payload = data.Slice(offset, definition.FixedSize);
            offset += definition.FixedSize;

            var partial = CreatePartialEvent(definition, packet.ThreadId, state.PendingScopePhase, state.PendingScopeTimestamp, payload);
            state.PendingScopePhase = TraceEventScopePhase.None;
            state.PendingScopeTimestamp = default;

            if (definition.MaybeHasAux)
            {
                ConsumeAux(ref partial, data, ref offset, state, events);
                if (!partial.IsComplete)
                {
                    state.PendingEvent = partial;
                    continue;
                }

                continue;
            }

            events.Add(FinalizeEvent(partial, state));
        }
    }

    private void HandleWellKnownMarker(ThreadState state, ReadOnlySpan<byte> data, ref int offset, List<TraceEvent> events)
    {
        var marker = data[offset] >> 1;
        switch (marker)
        {
            case 1: // AuxData
            case 3: // AuxDataTerminal
                throw new InvalidDataException("Unexpected aux marker without pending event.");

            case 4: // EnterScope
                state.PendingScopePhase = TraceEventScopePhase.Enter;
                state.PendingScopeTimestamp = CreateTimestamp(0);
                offset += 1;
                break;

            case 5: // LeaveScope
                offset += 1;
                EmitScopeLeave(state, CreateTimestamp(0), events);
                break;

            case 6: // EnterScope_TA
            {
                var timestamp = ReadStampedScopeTimestamp(data, ref offset, state, relativeToThreadBase: false);
                state.PendingScopePhase = TraceEventScopePhase.Enter;
                state.PendingScopeTimestamp = timestamp;
                break;
            }

            case 7: // LeaveScope_TA
            {
                var timestamp = ReadStampedScopeTimestamp(data, ref offset, state, relativeToThreadBase: false);
                EmitScopeLeave(state, timestamp, events);
                break;
            }

            case 8: // EnterScope_TB
            {
                var timestamp = ReadStampedScopeTimestamp(data, ref offset, state, relativeToThreadBase: true);
                state.PendingScopePhase = TraceEventScopePhase.Enter;
                state.PendingScopeTimestamp = timestamp;
                break;
            }

            case 9: // LeaveScope_TB
            {
                var timestamp = ReadStampedScopeTimestamp(data, ref offset, state, relativeToThreadBase: true);
                EmitScopeLeave(state, timestamp, events);
                break;
            }

            default:
                offset += 1;
                break;
        }
    }

    private TraceTimestamp ReadStampedScopeTimestamp(ReadOnlySpan<byte> data, ref int offset, ThreadState state, bool relativeToThreadBase)
    {
        EnsureAvailable(data, offset, 8, "stamped scope marker");
        var encoded = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
        offset += 8;

        var cycle = encoded >> 8;
        if (relativeToThreadBase)
        {
            cycle += state.BaseTimestampCycle != 0 ? state.BaseTimestampCycle : _startCycle;
        }

        var seconds = _secondsPerCycle.HasValue ? cycle * _secondsPerCycle.Value : 0.0;
        return CreateTimestamp(seconds, cycle);
    }

    private PartialTraceEvent CreatePartialEvent(
        TraceEventDefinition definition,
        uint threadId,
        TraceEventScopePhase scopePhase,
        TraceTimestamp scopeTimestamp,
        ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        ReadOnlyMemory<byte> attachment = ReadOnlyMemory<byte>.Empty;

        foreach (var field in definition.Fields)
        {
            if (field.IsAuxiliary)
            {
                continue;
            }

            var valueSize = field.FixedSize > 0 ? field.FixedSize : GetFieldTypeSize(field.TypeInfo);
            if (valueSize == 0 || field.Offset + valueSize > payload.Length)
            {
                continue;
            }

            var value = DecodeFixedValue(field.TypeInfo, payload.Slice(field.Offset, valueSize));
            if (!string.IsNullOrEmpty(field.Name))
            {
                fields[field.Name] = value;
            }
        }

        if (scopePhase == TraceEventScopePhase.Enter)
        {
            fields.TryAdd("TypeId", definition.Uid);
            fields.TryAdd("TypeName", definition.EventName);
        }

        var timestamp = scopePhase == TraceEventScopePhase.Enter
            ? scopeTimestamp
            : TryGetCycleTimestamp(fields, out var cycleTimestamp)
                ? cycleTimestamp
                : CreateTimestamp(0);

        return new PartialTraceEvent(definition, threadId, scopePhase, timestamp, fields, attachment);
    }

    private void ConsumeAux(
        ref PartialTraceEvent partial,
        ReadOnlySpan<byte> data,
        ref int offset,
        ThreadState state,
        List<TraceEvent> events)
    {
        while (offset < data.Length)
        {
            var markerByte = data[offset];
            var isAuxTerminal = markerByte is 3 or 6;
            var isAuxData = markerByte is 1 or 2;
            if (isAuxTerminal)
            {
                offset += 1;
                partial.IsComplete = true;
                events.Add(FinalizeEvent(partial, state));
                return;
            }

            if (!isAuxData)
            {
                partial.IsComplete = true;
                events.Add(FinalizeEvent(partial, state));
                return;
            }

            EnsureAvailable(data, offset, 4, "aux header");
            var auxPack = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var fieldIndex = (int)((auxPack >> 8) & 0x1F);
            var auxSize = (int)(auxPack >> 13);
            offset += 4;

            EnsureAvailable(data, offset, auxSize, "aux payload");
            var auxBytes = data.Slice(offset, auxSize).ToArray();
            offset += auxSize;

            if (fieldIndex < partial.Definition.Fields.Count)
            {
                var field = partial.Definition.Fields[fieldIndex];
                var decodedAux = DecodeAuxValue(field.TypeInfo, auxBytes);
                if (!string.IsNullOrEmpty(field.Name))
                {
                    partial.Fields[field.Name] = decodedAux;
                }
                else
                {
                    partial.Attachment = auxBytes;
                }

                if (field.Name == "Data" || field.Name == "Metadata")
                {
                    partial.Attachment = auxBytes;
                }
            }
            else
            {
                partial.Attachment = auxBytes;
            }
        }
    }

    private TraceEvent FinalizeEvent(PartialTraceEvent partial, ThreadState state)
    {
        var timestamp = partial.Timestamp;
        if (timestamp.Seconds == 0 && partial.ScopePhase != TraceEventScopePhase.Enter && TryGetCycleTimestamp(partial.Fields, out var cycleTimestamp))
        {
            timestamp = cycleTimestamp;
        }
        else if (timestamp.Seconds == 0 &&
                 partial.Definition.NoSync &&
                 state.BaseTimestampCycle != 0 &&
                 _secondsPerCycle.HasValue)
        {
            timestamp = CreateTimestamp(state.BaseTimestampCycle * _secondsPerCycle.Value, state.BaseTimestampCycle);
        }

        var descriptor = new TraceEventDescriptor(partial.Definition.LoggerName, partial.Definition.EventName, partial.ScopePhase);
        var traceEvent = new TraceEvent(descriptor, timestamp, partial.ThreadId, partial.Fields, partial.Attachment);

        if (partial.Definition.LoggerName == "$Trace" && partial.Definition.EventName == "NewTrace")
        {
            if (TryGetUInt64(partial.Fields, "StartCycle", out var startCycle))
            {
                _startCycle = startCycle;
            }

            if (TryGetUInt64(partial.Fields, "CycleFrequency", out var cycleFrequency) && cycleFrequency != 0)
            {
                _cycleFrequency = cycleFrequency;
                _secondsPerCycle = 1.0 / cycleFrequency;
            }
        }
        else if (partial.Definition.LoggerName == "$Trace" &&
                 partial.Definition.EventName == "ThreadTiming" &&
                 TryGetInt64(partial.Fields, "BaseTimestamp", out var baseTimestampOffset))
        {
            state.BaseTimestampCycle = ResolveBaseTimestampCycle(baseTimestampOffset);
        }

        if (partial.ScopePhase == TraceEventScopePhase.Enter)
        {
            state.ScopeStack.Push(descriptor);
        }

        return traceEvent;
    }

    private void EmitScopeLeave(ThreadState state, TraceTimestamp timestamp, List<TraceEvent> events)
    {
        if (state.ScopeStack.Count == 0)
        {
            return;
        }

        var descriptor = state.ScopeStack.Pop();
        events.Add(new TraceEvent(
            descriptor with { ScopePhase = TraceEventScopePhase.Leave },
            timestamp,
            state.ThreadId,
            new Dictionary<string, object?>(),
            ReadOnlyMemory<byte>.Empty));
    }

    private void RegisterDefinition(ReadOnlySpan<byte> payload)
    {
        EnsureAvailable(payload, 0, 6, "new event payload");

        var uid = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var fieldCount = payload[2];
        var flags = payload[3];
        var loggerNameSize = payload[4];
        var eventNameSize = payload[5];
        var descriptorSize = 8;
        var namesOffset = 6 + (fieldCount * descriptorSize);
        EnsureAvailable(payload, 0, namesOffset, "new event descriptors");

        var nameCursor = namesOffset;
        var loggerName = ReadAscii(payload, ref nameCursor, loggerNameSize);
        var eventName = ReadAscii(payload, ref nameCursor, eventNameSize);

        var fields = new List<TraceFieldDefinition>(fieldCount);
        var maxFixedSize = 0;

        for (var index = 0; index < fieldCount; index++)
        {
            var fieldOffset = 6 + (index * descriptorSize);
            var family = (TraceFieldFamily)payload[fieldOffset];
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(payload[(fieldOffset + 2)..]);
            var sizeOrRef = BinaryPrimitives.ReadUInt16LittleEndian(payload[(fieldOffset + 4)..]);
            var typeInfo = payload[fieldOffset + 6];
            var nameSize = family == TraceFieldFamily.DefinitionId ? (byte)0 : payload[fieldOffset + 7];
            var name = nameSize == 0 ? string.Empty : ReadAscii(payload, ref nameCursor, nameSize);

            var fixedSize = family switch
            {
                TraceFieldFamily.Regular => sizeOrRef,
                TraceFieldFamily.Reference => GetFieldTypeSize(typeInfo),
                TraceFieldFamily.DefinitionId => GetFieldTypeSize(typeInfo),
                _ => 0,
            };

            if (fixedSize > 0)
            {
                maxFixedSize = Math.Max(maxFixedSize, offset + fixedSize);
            }

            fields.Add(new TraceFieldDefinition(name, family, offset, fixedSize, typeInfo));
        }

        _definitions[uid] = new TraceEventDefinition(
            uid,
            loggerName,
            eventName,
            flags,
            maxFixedSize,
            fields);
    }

    private bool TryGetCycleTimestamp(IReadOnlyDictionary<string, object?> fields, out TraceTimestamp timestamp)
    {
        if (!_secondsPerCycle.HasValue)
        {
            timestamp = CreateTimestamp(0);
            return false;
        }

        if (!TryGetUInt64(fields, "Cycle", out var cycle))
        {
            timestamp = CreateTimestamp(0);
            return false;
        }

        timestamp = CreateTimestamp(cycle * _secondsPerCycle.Value, cycle);
        return true;
    }

    private static bool TryGetUInt64(IReadOnlyDictionary<string, object?> fields, string key, out ulong value)
    {
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            value = 0;
            return false;
        }

        switch (raw)
        {
            case ulong ulongValue:
                value = ulongValue;
                return true;
            case long longValue:
                value = (ulong)longValue;
                return true;
            case uint uintValue:
                value = uintValue;
                return true;
            case int intValue:
                value = (ulong)intValue;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryGetInt64(IReadOnlyDictionary<string, object?> fields, string key, out long value)
    {
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            value = 0;
            return false;
        }

        switch (raw)
        {
            case long longValue:
                value = longValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case uint uintValue:
                value = uintValue;
                return true;
            case ulong ulongValue:
                value = unchecked((long)ulongValue);
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private ulong ResolveBaseTimestampCycle(long baseTimestampOffset)
    {
        var startCycle = unchecked((long)_startCycle);
        var absoluteCycle = unchecked(startCycle + baseTimestampOffset);
        return absoluteCycle > 0 ? unchecked((ulong)absoluteCycle) : 0UL;
    }

    private object? DecodeFixedValue(byte typeInfo, ReadOnlySpan<byte> data)
    {
        if ((typeInfo & 0x08) != 0 || (typeInfo & 0x80) != 0)
        {
            return data.ToArray();
        }

        return typeInfo switch
        {
            0x00 => data[0],
            0x10 => unchecked((sbyte)data[0]),
            0x11 => BinaryPrimitives.ReadInt16LittleEndian(data),
            0x12 => BinaryPrimitives.ReadInt32LittleEndian(data),
            0x13 => BinaryPrimitives.ReadInt64LittleEndian(data),
            0x01 => BinaryPrimitives.ReadUInt16LittleEndian(data),
            0x02 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            0x03 => BinaryPrimitives.ReadUInt64LittleEndian(data),
            0x42 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)),
            0x43 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data)),
            _ => data.ToArray(),
        };
    }

    private static object DecodeAuxValue(byte typeInfo, byte[] data)
    {
        if ((typeInfo & 0x08) != 0)
        {
            return NormalizeDecodedString((typeInfo & 0x03) == 1
                ? Encoding.Unicode.GetString(data)
                : Encoding.UTF8.GetString(data));
        }

        return data;
    }

    private static string NormalizeDecodedString(string value)
    {
        return value.TrimEnd('\0');
    }

    private TraceTimestamp CreateTimestamp(double seconds, ulong? cycle = null)
    {
        return new TraceTimestamp(seconds, cycle, _secondsPerCycle);
    }

    private static string ReadAscii(ReadOnlySpan<byte> payload, ref int offset, int length)
    {
        EnsureAvailable(payload, offset, length, "ascii name");
        var value = Encoding.ASCII.GetString(payload.Slice(offset, length));
        offset += length;
        return value;
    }

    private static int GetFieldTypeSize(byte typeInfo)
    {
        var sizeBits = typeInfo & 0x03;
        var isString = (typeInfo & 0x08) != 0;
        var isArray = (typeInfo & 0x80) != 0;
        if (isString || isArray)
        {
            return 0;
        }

        return sizeBits switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 0,
        };
    }

    private ThreadState GetOrAddThreadState(ushort threadId)
    {
        if (_threadStates.TryGetValue(threadId, out var state))
        {
            return state;
        }

        state = new ThreadState(threadId);
        _threadStates.Add(threadId, state);
        return state;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int required, string label)
    {
        if (offset < 0 || required < 0 || offset + required > data.Length)
        {
            throw new InvalidDataException($"Trace {label} is truncated.");
        }
    }

    private readonly record struct TraceEventDefinition(
        ushort Uid,
        string LoggerName,
        string EventName,
        byte Flags,
        int FixedSize,
        IReadOnlyList<TraceFieldDefinition> Fields)
    {
        public bool Important => (Flags & 0x01) != 0;

        public bool MaybeHasAux => (Flags & 0x02) != 0;

        public bool NoSync => (Flags & 0x04) != 0;

        public bool IsDefinition => (Flags & 0x08) != 0;
    }

    private readonly record struct TraceFieldDefinition(
        string Name,
        TraceFieldFamily Family,
        int Offset,
        int FixedSize,
        byte TypeInfo)
    {
        public bool IsAuxiliary => FixedSize == 0;
    }

    private sealed class PartialTraceEvent
    {
        public PartialTraceEvent(
            TraceEventDefinition definition,
            uint threadId,
            TraceEventScopePhase scopePhase,
            TraceTimestamp timestamp,
            Dictionary<string, object?> fields,
            ReadOnlyMemory<byte> attachment)
        {
            Definition = definition;
            ThreadId = threadId;
            ScopePhase = scopePhase;
            Timestamp = timestamp;
            Fields = fields;
            Attachment = attachment;
        }

        public TraceEventDefinition Definition { get; }

        public uint ThreadId { get; }

        public TraceEventScopePhase ScopePhase { get; }

        public TraceTimestamp Timestamp { get; }

        public Dictionary<string, object?> Fields { get; }

        public ReadOnlyMemory<byte> Attachment { get; set; }

        public bool IsComplete { get; set; }
    }

    private sealed class ThreadState
    {
        public ThreadState(ushort threadId)
        {
            ThreadId = threadId;
        }

        public ushort ThreadId { get; }

        public Stack<TraceEventDescriptor> ScopeStack { get; } = new();

        public TraceEventScopePhase PendingScopePhase { get; set; }

        public TraceTimestamp PendingScopeTimestamp { get; set; }

        public PartialTraceEvent? PendingEvent { get; set; }

        public ulong BaseTimestampCycle { get; set; }
    }

    private enum TraceFieldFamily : byte
    {
        Regular = 0,
        Reference = 1,
        DefinitionId = 2,
    }
}
