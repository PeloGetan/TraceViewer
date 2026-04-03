namespace TraceViewer.TraceFormat;

public sealed class TraceReadResult
{
    public TraceReadResult(
        IReadOnlyList<TraceEvent> events,
        TraceFileHeader? fileHeader = null,
        IReadOnlyList<TracePacket>? packets = null,
        int? eventCount = null,
        int? packetCount = null)
    {
        Events = events;
        FileHeader = fileHeader;
        Packets = packets ?? Array.Empty<TracePacket>();
        EventCount = eventCount ?? Events.Count;
        PacketCount = packetCount ?? Packets.Count;
    }

    public IReadOnlyList<TraceEvent> Events { get; }

    public TraceFileHeader? FileHeader { get; }

    public IReadOnlyList<TracePacket> Packets { get; }

    public int EventCount { get; }

    public int PacketCount { get; }
}
