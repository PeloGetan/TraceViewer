using K4os.Compression.LZ4;
using System.Text;
using UEProfileReader.TraceFormat;

namespace UEProfileReader.Tests;

public sealed class UTraceFileReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    [Fact]
    public void Read_ParsesHandshakeAndUncompressedPacket()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(17, payload),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        Assert.NotNull(result.FileHeader);
        Assert.Equal<ushort>(40, result.FileHeader!.MetadataSize);
        Assert.Equal<ushort>(1981, result.FileHeader.ControlPort);
        Assert.Equal<byte>(4, result.FileHeader.TransportVersion);
        Assert.Equal<byte>(7, result.FileHeader.ProtocolVersion);
        Assert.Equal(16, result.FileHeader.SessionGuid.Length);
        Assert.Equal(16, result.FileHeader.TraceGuid.Length);

        Assert.Collection(
            result.Packets,
            packet =>
            {
                Assert.Equal<ushort>(17, packet.ThreadId);
                Assert.False(packet.IsEncoded);
                Assert.False(packet.HasVerificationTrailer);
                Assert.Null(packet.DecodedSize);
                Assert.Equal(payload, packet.Payload.ToArray());
            });
    }

    [Fact]
    public void Read_DecodesEncodedPacket()
    {
        var payload = Enumerable.Range(0, 512).Select(index => (byte)(index % 13)).ToArray();
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                EncodedPacket(9, payload),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        Assert.Collection(
            result.Packets,
            packet =>
            {
                Assert.Equal<ushort>(9, packet.ThreadId);
                Assert.True(packet.IsEncoded);
                Assert.Equal<ushort?>((ushort)payload.Length, packet.DecodedSize);
                Assert.Equal(payload, packet.Payload.ToArray());
            });
    }

    [Fact]
    public void Read_DecodesNewTraceAndRegularEvent()
    {
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(0, BuildDefinitionsPayload(
                    new EventSpec(
                        1,
                        "$Trace",
                        "NewTrace",
                        Flags: 0x05,
                        Fields:
                        [
                            new FieldSpec("StartCycle", 0, 0x03, 0, 8),
                            new FieldSpec("CycleFrequency", 0, 0x03, 8, 8),
                        ]),
                    new EventSpec(
                        2,
                        "Misc",
                        "BeginFrame",
                        Flags: 0x04,
                        Fields:
                        [
                            new FieldSpec("Cycle", 0, 0x03, 0, 8),
                            new FieldSpec("FrameType", 0, 0x00, 8, 1),
                        ]))),
                Packet(1, BuildImportantEventPayload(
                    1,
                    UInt64Field(100UL),
                    UInt64Field(1_000UL))),
                Packet(2, BuildNoSyncEventPayload(
                    2,
                    UInt64Field(500UL),
                    ByteField(1))),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        var beginFrame = Assert.Single(result.Events, evt => evt.Descriptor.Logger == "Misc" && evt.Descriptor.EventName == "BeginFrame");
        Assert.Equal(0.5d, beginFrame.Timestamp.Seconds, 3);
        Assert.Equal<ulong>(500, Convert.ToUInt64(beginFrame.Fields["Cycle"]));
        Assert.Equal((byte)1, Assert.IsType<byte>(beginFrame.Fields["FrameType"]));
        Assert.True(beginFrame.Timestamp.SecondsPerCycle.HasValue);
        Assert.Equal(0.001d, beginFrame.Timestamp.SecondsPerCycle.Value, 6);
    }

    [Fact]
    public void Read_DecodesImportantEventWithAuxStringField()
    {
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(0, BuildDefinitionsPayload(
                    new EventSpec(
                        4,
                        "CpuProfiler",
                        "EventSpec",
                        Flags: 0x07,
                        Fields:
                        [
                            new FieldSpec("Id", 0, 0x02, 0, 4),
                            new FieldSpec("Name", 0, 0x08, 4, 0),
                        ]))),
                Packet(1, BuildImportantEventPayload(
                    4,
                    UInt32Field(123u),
                    ImportantAuxField(1, Encoding.UTF8.GetBytes("GameThread")))),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        var eventSpec = Assert.Single(result.Events, evt => evt.Descriptor.Logger == "CpuProfiler" && evt.Descriptor.EventName == "EventSpec");
        Assert.Equal<uint>(123u, Convert.ToUInt32(eventSpec.Fields["Id"]));
        Assert.Equal("GameThread", Assert.IsType<string>(eventSpec.Fields["Name"]));
    }

    [Fact]
    public void Read_TrimsTrailingNullFromAuxStringField()
    {
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(0, BuildDefinitionsPayload(
                    new EventSpec(
                        5,
                        "$Trace",
                        "ThreadInfo",
                        Flags: 0x07,
                        Fields:
                        [
                            new FieldSpec("ThreadId", 0, 0x02, 0, 4),
                            new FieldSpec("SystemId", 0, 0x02, 4, 4),
                            new FieldSpec("SortHint", 0, 0x12, 8, 4),
                            new FieldSpec("Name", 0, 0x08, 12, 0),
                        ]))),
                Packet(1, BuildImportantEventPayload(
                    5,
                    UInt32Field(7u),
                    UInt32Field(77u),
                    Int32Field(12),
                    ImportantAuxField(3, Encoding.UTF8.GetBytes("RenderThread\0")))),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        var threadInfo = Assert.Single(result.Events, evt => evt.Descriptor.Logger == "$Trace" && evt.Descriptor.EventName == "ThreadInfo");
        Assert.Equal("RenderThread", Assert.IsType<string>(threadInfo.Fields["Name"]));
    }

    [Fact]
    public void Read_DecodesScopedEventWithAuxPayload()
    {
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(0, BuildDefinitionsPayload(
                    new EventSpec(
                        1,
                        "$Trace",
                        "NewTrace",
                        Flags: 0x05,
                        Fields:
                        [
                            new FieldSpec("StartCycle", 0, 0x03, 0, 8),
                            new FieldSpec("CycleFrequency", 0, 0x03, 8, 8),
                        ]),
                    new EventSpec(
                        3,
                        "Cpu",
                        "WaitScope",
                        Flags: 0x06,
                        Fields:
                        [
                            new FieldSpec("Payload", 0, 0x80, 0, 0),
                        ]))),
                Packet(1, BuildImportantEventPayload(
                    1,
                    UInt64Field(0UL),
                    UInt64Field(1_000UL))),
                Packet(2, BuildRawPayloadBytes(
                    ScopeMarker(8, 200UL),
                    BuildNoSyncEventPayload(3),
                    AuxField(0, new byte[] { 9, 8, 7 }),
                    AuxTerminal(),
                    ScopeMarker(9, 260UL))),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        var cpuEvents = result.Events.Where(evt => evt.Descriptor.Logger == "Cpu").ToArray();
        Assert.Contains(cpuEvents, evt => evt.Descriptor.ScopePhase == TraceEventScopePhase.Enter);
        var enterEvent = cpuEvents.First(evt => evt.Descriptor.ScopePhase == TraceEventScopePhase.Enter);
        Assert.Equal("WaitScope", enterEvent.Descriptor.EventName);
        Assert.Equal(0.2d, enterEvent.Timestamp.Seconds, 3);
        Assert.Equal<ushort>(3, (ushort)Convert.ToUInt32(enterEvent.Fields["TypeId"]));
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.IsType<byte[]>(enterEvent.Fields["Payload"]));

        Assert.Contains(cpuEvents, evt => evt.Descriptor.ScopePhase == TraceEventScopePhase.Leave);
        var leaveEvent = cpuEvents.First(evt => evt.Descriptor.ScopePhase == TraceEventScopePhase.Leave);
        Assert.Equal("WaitScope", leaveEvent.Descriptor.EventName);
        Assert.Equal(0.26d, leaveEvent.Timestamp.Seconds, 3);
    }

    [Fact]
    public void Read_AssignsThreadTimingBaseTimestampToNoSyncEvents()
    {
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(0, BuildDefinitionsPayload(
                    new EventSpec(
                        1,
                        "$Trace",
                        "NewTrace",
                        Flags: 0x05,
                        Fields:
                        [
                            new FieldSpec("StartCycle", 0, 0x03, 0, 8),
                            new FieldSpec("CycleFrequency", 0, 0x03, 8, 8),
                        ]),
                    new EventSpec(
                        2,
                        "$Trace",
                        "ThreadTiming",
                        Flags: 0x04,
                        Fields:
                        [
                            new FieldSpec("BaseTimestamp", 0, 0x03, 0, 8),
                        ]),
                    new EventSpec(
                        3,
                        "CpuProfiler",
                        "EventBatchV3",
                        Flags: 0x06,
                        Fields:
                        [
                            new FieldSpec("Data", 0, 0x80, 0, 0),
                        ]))),
                Packet(1, BuildImportantEventPayload(
                    1,
                    UInt64Field(1_000_000UL),
                    UInt64Field(1_000UL))),
                Packet(2, BuildRawPayloadBytes(
                    BuildNoSyncEventPayload(2, UInt64Field(10_000UL)),
                    BuildNoSyncEventPayload(3),
                    AuxField(0, new byte[] { 1, 2, 3 }),
                    AuxTerminal())),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        var eventBatch = Assert.Single(result.Events, evt => evt.Descriptor.Logger == "CpuProfiler" && evt.Descriptor.EventName == "EventBatchV3");
        Assert.True(eventBatch.Timestamp.Cycle.HasValue);
        Assert.Equal(1_010_000UL, eventBatch.Timestamp.Cycle.Value);
        Assert.Equal(1_010.0d, eventBatch.Timestamp.Seconds, 3);
        Assert.Equal(new byte[] { 1, 2, 3 }, eventBatch.Attachment.ToArray());
    }

    [Fact]
    public void Read_ResolvesNegativeThreadTimingOffsetAgainstStartCycle()
    {
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(0, BuildDefinitionsPayload(
                    new EventSpec(
                        1,
                        "$Trace",
                        "NewTrace",
                        Flags: 0x05,
                        Fields:
                        [
                            new FieldSpec("StartCycle", 0, 0x03, 0, 8),
                            new FieldSpec("CycleFrequency", 0, 0x03, 8, 8),
                        ]),
                    new EventSpec(
                        2,
                        "$Trace",
                        "ThreadTiming",
                        Flags: 0x04,
                        Fields:
                        [
                            new FieldSpec("BaseTimestamp", 0, 0x03, 0, 8),
                        ]),
                    new EventSpec(
                        3,
                        "CpuProfiler",
                        "EventBatchV3",
                        Flags: 0x06,
                        Fields:
                        [
                            new FieldSpec("Data", 0, 0x80, 0, 0),
                        ]))),
                Packet(1, BuildImportantEventPayload(
                    1,
                    UInt64Field(1_000_000UL),
                    UInt64Field(1_000UL))),
                Packet(2, BuildRawPayloadBytes(
                    BuildNoSyncEventPayload(2, UInt64Field(unchecked((ulong)-40L))),
                    BuildNoSyncEventPayload(3),
                    AuxField(0, new byte[] { 4, 5, 6 }),
                    AuxTerminal())),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        var eventBatch = Assert.Single(result.Events, evt => evt.Descriptor.Logger == "CpuProfiler" && evt.Descriptor.EventName == "EventBatchV3");
        Assert.True(eventBatch.Timestamp.Cycle.HasValue);
        Assert.Equal(999_960UL, eventBatch.Timestamp.Cycle.Value);
        Assert.Equal(999.96d, eventBatch.Timestamp.Seconds, 3);
    }

    [Fact]
    public void Read_DecodesThreadBaseRelativeScopeMarkers()
    {
        var tracePath = CreateTraceFile(
            transportVersion: 4,
            protocolVersion: 7,
            packets:
            [
                Packet(0, BuildDefinitionsPayload(
                    new EventSpec(
                        1,
                        "$Trace",
                        "NewTrace",
                        Flags: 0x05,
                        Fields:
                        [
                            new FieldSpec("StartCycle", 0, 0x03, 0, 8),
                            new FieldSpec("CycleFrequency", 0, 0x03, 8, 8),
                        ]),
                    new EventSpec(
                        2,
                        "$Trace",
                        "ThreadTiming",
                        Flags: 0x04,
                        Fields:
                        [
                            new FieldSpec("BaseTimestamp", 0, 0x03, 0, 8),
                        ]),
                    new EventSpec(
                        3,
                        "Cpu",
                        "WaitScope",
                        Flags: 0x06,
                        Fields:
                        [
                            new FieldSpec("Payload", 0, 0x80, 0, 0),
                        ]))),
                Packet(1, BuildImportantEventPayload(
                    1,
                    UInt64Field(1_000_000UL),
                    UInt64Field(1_000UL))),
                Packet(2, BuildRawPayloadBytes(
                    BuildNoSyncEventPayload(2, UInt64Field(500UL)),
                    ScopeMarker(8, 25UL),
                    BuildNoSyncEventPayload(3),
                    AuxField(0, new byte[] { 7, 8 }),
                    AuxTerminal(),
                    ScopeMarker(9, 30UL))),
            ]);

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        var cpuEvents = result.Events.Where(evt => evt.Descriptor.Logger == "Cpu").ToArray();
        Assert.Collection(
            cpuEvents,
            enter =>
            {
                Assert.Equal(TraceEventScopePhase.Enter, enter.Descriptor.ScopePhase);
                Assert.Equal(1_000_525UL, enter.Timestamp.Cycle);
                Assert.Equal(1000.525d, enter.Timestamp.Seconds, 3);
            },
            leave =>
            {
                Assert.Equal(TraceEventScopePhase.Leave, leave.Descriptor.ScopePhase);
                Assert.Equal(1_000_530UL, leave.Timestamp.Cycle);
                Assert.Equal(1000.530d, leave.Timestamp.Seconds, 3);
            });
    }

    [Fact]
    public void Read_ThrowsOnInvalidMagic()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.utrace");
        using (var stream = File.Open(filePath, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x12345678u);
        }

        _tempFiles.Add(filePath);
        var reader = new UTraceFileReader();

        var exception = Assert.Throws<InvalidDataException>(() => reader.Read(filePath));
        Assert.Contains("Unexpected trace magic", exception.Message);
    }

    [Fact]
    public void Read_LocalSampleTrace_ParsesTransportEnvelope()
    {
        var tracePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "20260315_164055.utrace"));
        if (!File.Exists(tracePath))
        {
            return;
        }

        var reader = new UTraceFileReader();
        var result = reader.Read(tracePath);

        Assert.NotNull(result.FileHeader);
        Assert.Equal<byte>(4, result.FileHeader!.TransportVersion);
        Assert.Equal<byte>(7, result.FileHeader.ProtocolVersion);
        Assert.NotEmpty(result.Packets);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private string CreateTraceFile(byte transportVersion, byte protocolVersion, params PacketSpec[] packets)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.utrace");
        using (var stream = File.Open(filePath, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            WriteHandshake(writer, transportVersion, protocolVersion);
            foreach (var packet in packets)
            {
                packet.Write(writer);
            }
        }

        _tempFiles.Add(filePath);
        return filePath;
    }

    private static void WriteHandshake(BinaryWriter writer, byte transportVersion, byte protocolVersion)
    {
        writer.Write(0x54524332u);
        writer.Write((ushort)40);

        writer.Write((ushort)(sizeof(ushort) | (0 << 8)));
        writer.Write((ushort)1981);

        writer.Write((ushort)(16 | (1 << 8)));
        writer.Write(Enumerable.Range(1, 16).Select(index => (byte)index).ToArray());

        writer.Write((ushort)(16 | (2 << 8)));
        writer.Write(Enumerable.Range(17, 16).Select(index => (byte)index).ToArray());

        writer.Write(transportVersion);
        writer.Write(protocolVersion);
    }

    private static PacketSpec Packet(ushort threadId, byte[] payload)
    {
        return new PacketSpec(threadId, payload, isEncoded: false);
    }

    private static byte[] BuildRawPayloadBytes(params byte[][] chunks)
    {
        return Combine(chunks);
    }

    private static PacketSpec EncodedPacket(ushort threadId, byte[] payload)
    {
        return new PacketSpec(threadId, payload, isEncoded: true);
    }

    private sealed class PacketSpec
    {
        private readonly ushort _threadId;
        private readonly byte[] _payload;
        private readonly bool _isEncoded;

        public PacketSpec(ushort threadId, byte[] payload, bool isEncoded)
        {
            _threadId = threadId;
            _payload = payload;
            _isEncoded = isEncoded;
        }

        public void Write(BinaryWriter writer)
        {
            if (!_isEncoded)
            {
                writer.Write((ushort)(4 + _payload.Length));
                writer.Write(_threadId);
                writer.Write(_payload);
                return;
            }

            var compressed = new byte[LZ4Codec.MaximumOutputSize(_payload.Length)];
            var compressedSize = LZ4Codec.Encode(
                _payload,
                0,
                _payload.Length,
                compressed,
                0,
                compressed.Length);

            Assert.True(compressedSize > 0);

            writer.Write((ushort)(6 + compressedSize));
            writer.Write((ushort)(_threadId | 0x8000));
            writer.Write((ushort)_payload.Length);
            writer.Write(compressed, 0, compressedSize);
        }
    }

    private sealed record EventSpec(
        ushort Uid,
        string Logger,
        string EventName,
        byte Flags,
        IReadOnlyList<FieldSpec> Fields);

    private sealed record FieldSpec(
        string Name,
        byte Family,
        byte TypeInfo,
        ushort Offset,
        ushort Size);

    private static byte[] BuildDefinitionsPayload(params EventSpec[] definitions)
    {
        var chunks = definitions.Select(BuildDefinitionEventPayload).ToArray();
        return Combine(chunks);
    }

    private static byte[] BuildDefinitionEventPayload(EventSpec definition)
    {
        var payload = new List<byte>();
        WriteUInt16(payload, definition.Uid);
        payload.Add((byte)definition.Fields.Count);
        payload.Add(definition.Flags);
        payload.Add((byte)definition.Logger.Length);
        payload.Add((byte)definition.EventName.Length);

        foreach (var field in definition.Fields)
        {
            payload.Add(field.Family);
            payload.Add(0);
            WriteUInt16(payload, field.Offset);
            WriteUInt16(payload, field.Size);
            payload.Add(field.TypeInfo);
            payload.Add((byte)field.Name.Length);
        }

        payload.AddRange(Encoding.ASCII.GetBytes(definition.Logger));
        payload.AddRange(Encoding.ASCII.GetBytes(definition.EventName));
        foreach (var field in definition.Fields)
        {
            payload.AddRange(Encoding.ASCII.GetBytes(field.Name));
        }

        if ((payload.Count & 1) != 0)
        {
            payload.Add(0);
        }

        return BuildImportantEventPayload(0, payload.ToArray());
    }

    private static byte[] BuildImportantEventPayload(ushort uid, params byte[][] chunks)
    {
        var body = Combine(chunks);
        var payload = new List<byte>();
        WriteUInt16(payload, uid);
        WriteUInt16(payload, (ushort)body.Length);
        payload.AddRange(body);
        return payload.ToArray();
    }

    private static byte[] BuildNoSyncEventPayload(ushort uid, params byte[][] fixedPayloadParts)
    {
        var payload = new List<byte>();
        WriteUInt16(payload, (ushort)((uid << 1) | 1));
        payload.AddRange(Combine(fixedPayloadParts));
        return payload.ToArray();
    }

    private static byte[] ScopeMarker(byte markerId, ulong cycle)
    {
        var value = (cycle << 8) | ((ulong)markerId << 1);
        return BitConverter.GetBytes(value);
    }

    private static byte[] AuxField(byte fieldIndex, byte[] bytes)
    {
        var pack = (uint)(bytes.Length << 13) | (uint)(fieldIndex << 8) | 2u;
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(pack));
        payload.AddRange(bytes);
        return payload.ToArray();
    }

    private static byte[] ImportantAuxField(byte fieldIndex, byte[] bytes)
    {
        var pack = (uint)(bytes.Length << 13) | (uint)(fieldIndex << 8) | 1u;
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(pack));
        payload.AddRange(bytes);
        payload.Add(3);
        return payload.ToArray();
    }

    private static byte[] AuxTerminal()
    {
        return new byte[] { 6 };
    }

    private static byte[] UInt32Field(uint value)
    {
        return BitConverter.GetBytes(value);
    }

    private static byte[] Int32Field(int value)
    {
        return BitConverter.GetBytes(value);
    }

    private static byte[] UInt64Field(ulong value)
    {
        return BitConverter.GetBytes(value);
    }

    private static byte[] ByteField(byte value)
    {
        return new[] { value };
    }

    private static void WriteUInt16(List<byte> bytes, ushort value)
    {
        bytes.AddRange(BitConverter.GetBytes(value));
    }

    private static byte[] Combine(params byte[][] chunks)
    {
        var bytes = new List<byte>();
        foreach (var chunk in chunks)
        {
            bytes.AddRange(chunk);
        }

        return bytes.ToArray();
    }
}
