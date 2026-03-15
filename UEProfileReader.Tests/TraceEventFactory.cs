using UEProfileReader.TraceFormat;

namespace UEProfileReader.Tests;

internal static class TraceEventFactory
{
    public static TraceEvent Create(
        string logger,
        string eventName,
        double timestamp,
        uint threadId = 0,
        ulong? cycle = null,
        TraceEventScopePhase scopePhase = TraceEventScopePhase.None,
        IReadOnlyDictionary<string, object?>? fields = null,
        ReadOnlyMemory<byte> attachment = default)
    {
        return new TraceEvent(
            new TraceEventDescriptor(logger, eventName, scopePhase),
            new TraceTimestamp(timestamp, cycle),
            threadId,
            fields ?? new Dictionary<string, object?>(),
            attachment);
    }
}
