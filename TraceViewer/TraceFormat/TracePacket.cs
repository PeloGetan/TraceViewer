namespace TraceViewer.TraceFormat;

public sealed class TracePacket
{
    public required ushort ThreadId { get; init; }

    public required bool IsEncoded { get; init; }

    public required bool HasVerificationTrailer { get; init; }

    public required ushort PacketSize { get; init; }

    public ushort? DecodedSize { get; init; }

    public required ReadOnlyMemory<byte> Payload { get; init; }
}
