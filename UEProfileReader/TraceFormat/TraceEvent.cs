namespace UEProfileReader.TraceFormat;

public sealed record TraceEvent(
    TraceEventDescriptor Descriptor,
    TraceTimestamp Timestamp,
    uint ThreadId,
    IReadOnlyDictionary<string, object?> Fields,
    ReadOnlyMemory<byte> Attachment);
