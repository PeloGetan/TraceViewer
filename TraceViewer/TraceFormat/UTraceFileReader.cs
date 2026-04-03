using System.Buffers.Binary;
using System.IO;
using K4os.Compression.LZ4;

namespace TraceViewer.TraceFormat;

public sealed class UTraceFileReader : ITraceReader
{
    private const uint HandshakeMagic = 0x54524332; // "2CRT"
    private const int HandshakePrefixSize = 6;
    private const ushort EncodedMarker = 0x8000;
    private const ushort VerificationMarker = 0x4000;
    private const ushort ThreadIdMask = 0x3FFF;
    private const int RawPacketHeaderSize = 4;
    private const int EncodedPacketHeaderSize = 6;
    private const int VerificationTrailerSize = 8;

    public TraceReadResult Read(string traceFilePath)
    {
        return Read(traceFilePath, null, retainEvents: true, retainPackets: true);
    }

    public TraceReadResult Read(
        string traceFilePath,
        Action<TraceEvent>? eventSink,
        bool retainEvents,
        bool retainPackets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceFilePath);

        using var stream = File.OpenRead(traceFilePath);
        return Read(stream, eventSink, retainEvents, retainPackets);
    }

    internal TraceReadResult Read(Stream stream)
    {
        return Read(stream, null, retainEvents: true, retainPackets: true);
    }

    internal TraceReadResult Read(
        Stream stream,
        Action<TraceEvent>? eventSink,
        bool retainEvents,
        bool retainPackets)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var header = ReadHeader(reader);
        var packets = ReadPackets(reader);
        var decodeResult = new TraceEventStreamDecoder(packets).Decode(eventSink, retainEvents);
        var packetsToReturn = retainPackets ? packets : Array.Empty<TracePacket>();

        return new TraceReadResult(decodeResult.Events, header, packetsToReturn, decodeResult.EventCount, packets.Count);
    }

    private static TraceFileHeader ReadHeader(BinaryReader reader)
    {
        var magic = reader.ReadUInt32();
        if (magic != HandshakeMagic)
        {
            throw new InvalidDataException($"Unexpected trace magic 0x{magic:X8}.");
        }

        var metadataSize = reader.ReadUInt16();
        var metadata = ReadExact(reader, metadataSize);
        var controlPort = (ushort)0;
        var sessionGuid = Array.Empty<byte>();
        var traceGuid = Array.Empty<byte>();

        var offset = 0;
        while (offset < metadata.Length)
        {
            if (metadata.Length - offset < sizeof(ushort))
            {
                throw new InvalidDataException("Trace metadata field header is truncated.");
            }

            var metadataField = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(offset, sizeof(ushort)));
            offset += sizeof(ushort);

            var fieldSize = metadataField & 0xFF;
            var fieldId = metadataField >> 8;
            if (metadata.Length - offset < fieldSize)
            {
                throw new InvalidDataException("Trace metadata field payload is truncated.");
            }

            var fieldData = metadata.AsSpan(offset, fieldSize);
            offset += fieldSize;

            switch (fieldId)
            {
                case 0 when fieldSize == sizeof(ushort):
                    controlPort = BinaryPrimitives.ReadUInt16LittleEndian(fieldData);
                    break;

                case 1:
                    sessionGuid = fieldData.ToArray();
                    break;

                case 2:
                    traceGuid = fieldData.ToArray();
                    break;
            }
        }

        var transportVersion = reader.ReadByte();
        var protocolVersion = reader.ReadByte();

        return new TraceFileHeader
        {
            MetadataSize = metadataSize,
            ControlPort = controlPort,
            SessionGuid = sessionGuid,
            TraceGuid = traceGuid,
            TransportVersion = transportVersion,
            ProtocolVersion = protocolVersion,
        };
    }

    private static IReadOnlyList<TracePacket> ReadPackets(BinaryReader reader)
    {
        var packets = new List<TracePacket>();

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            if (reader.BaseStream.Length - reader.BaseStream.Position < RawPacketHeaderSize)
            {
                throw new InvalidDataException("Trace packet header is truncated.");
            }

            var packetSize = reader.ReadUInt16();
            var threadInfo = reader.ReadUInt16();
            var isEncoded = (threadInfo & EncodedMarker) != 0;
            var hasVerificationTrailer = (threadInfo & VerificationMarker) != 0;
            var threadId = (ushort)(threadInfo & ThreadIdMask);
            var headerSize = isEncoded ? EncodedPacketHeaderSize : RawPacketHeaderSize;

            if (packetSize < headerSize)
            {
                throw new InvalidDataException($"Invalid packet size {packetSize}.");
            }

            ushort? decodedSize = null;
            if (isEncoded)
            {
                decodedSize = reader.ReadUInt16();
            }

            var payloadSize = packetSize - headerSize;
            if (reader.BaseStream.Length - reader.BaseStream.Position < payloadSize)
            {
                throw new InvalidDataException("Trace packet payload is truncated.");
            }

            var payload = ReadExact(reader, payloadSize);
            ReadOnlyMemory<byte> decodedPayload = payload;
            if (isEncoded)
            {
                decodedPayload = DecodePayload(payload, decodedSize!.Value);
            }

            if (hasVerificationTrailer)
            {
                if (reader.BaseStream.Length - reader.BaseStream.Position < VerificationTrailerSize)
                {
                    throw new InvalidDataException("Trace verification trailer is truncated.");
                }

                _ = reader.ReadUInt64();
            }

            packets.Add(new TracePacket
            {
                ThreadId = threadId,
                IsEncoded = isEncoded,
                HasVerificationTrailer = hasVerificationTrailer,
                PacketSize = packetSize,
                DecodedSize = decodedSize,
                Payload = decodedPayload,
            });
        }

        return packets;
    }

    private static ReadOnlyMemory<byte> DecodePayload(byte[] payload, ushort decodedSize)
    {
        var decoded = new byte[decodedSize];
        var actualSize = LZ4Codec.Decode(payload, 0, payload.Length, decoded, 0, decoded.Length);
        if (actualSize != decodedSize)
        {
            throw new InvalidDataException($"Decoded payload size mismatch. Expected {decodedSize}, got {actualSize}.");
        }

        return decoded;
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var data = reader.ReadBytes(count);
        if (data.Length != count)
        {
            throw new InvalidDataException($"Unexpected end of stream. Expected {count} bytes, got {data.Length}.");
        }

        return data;
    }
}
