namespace UEProfileReader.TraceFormat;

public sealed class TraceFileHeader
{
    public required ushort MetadataSize { get; init; }

    public ushort ControlPort { get; init; }

    public required byte[] SessionGuid { get; init; }

    public required byte[] TraceGuid { get; init; }

    public byte TransportVersion { get; init; }

    public byte ProtocolVersion { get; init; }
}
