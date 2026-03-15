namespace UEProfileReader.TraceFormat;

public sealed class TraceReadResult
{
    public TraceReadResult(
        IReadOnlyList<TraceEvent> events,
        TraceFileHeader? fileHeader = null,
        IReadOnlyList<TracePacket>? packets = null)
    {
        Events = events;
        FileHeader = fileHeader;
        Packets = packets ?? Array.Empty<TracePacket>();
    }

    public IReadOnlyList<TraceEvent> Events { get; }

    public TraceFileHeader? FileHeader { get; }

    public IReadOnlyList<TracePacket> Packets { get; }
}
