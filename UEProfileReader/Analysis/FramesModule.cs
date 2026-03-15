using UEProfileReader.SessionModel;
using UEProfileReader.TraceFormat;

namespace UEProfileReader.Analysis;

public sealed class FramesModule : ITraceAnalysisModule
{
    private static readonly HashSet<string> SupportedEvents =
    [
        "BeginFrame",
        "EndFrame",
        "BeginGameFrame",
        "EndGameFrame",
        "BeginRenderFrame",
        "EndRenderFrame",
    ];

    private readonly ulong[] _lastFrameCycles = new ulong[2];

    public string Name => "Frames";

    public void Initialize(IAnalysisContext context)
    {
        Array.Clear(_lastFrameCycles);
    }

    public bool CanHandle(in TraceEvent traceEvent)
    {
        return traceEvent.Descriptor.Logger == "Misc" && SupportedEvents.Contains(traceEvent.Descriptor.EventName);
    }

    public void Process(in TraceEvent traceEvent, IAnalysisContext context)
    {
        var session = context.Session;
        if (!TryResolveTimestamp(traceEvent, out var timestamp))
        {
            return;
        }

        switch (traceEvent.Descriptor.EventName)
        {
            case "BeginFrame":
                session.Frames.BeginFrame(ReadFrameType(traceEvent), timestamp);
                break;
            case "EndFrame":
                session.Frames.EndFrame(ReadFrameType(traceEvent), timestamp);
                break;
            case "BeginGameFrame":
                session.Frames.BeginFrame(FrameType.Game, timestamp);
                break;
            case "EndGameFrame":
                session.Frames.EndFrame(FrameType.Game, timestamp);
                break;
            case "BeginRenderFrame":
                session.Frames.BeginFrame(FrameType.Rendering, timestamp);
                break;
            case "EndRenderFrame":
                session.Frames.EndFrame(FrameType.Rendering, timestamp);
                break;
            default:
                return;
        }

        session.UpdateDuration(timestamp);
    }

    public void Complete(IAnalysisContext context)
    {
    }

    private static FrameType ReadFrameType(TraceEvent traceEvent)
    {
        if (!traceEvent.Fields.TryGetValue("FrameType", out var value) || value is null)
        {
            return FrameType.Game;
        }

        return value switch
        {
            FrameType frameType => frameType,
            int intValue when intValue == 1 => FrameType.Rendering,
            uint uintValue when uintValue == 1 => FrameType.Rendering,
            byte byteValue when byteValue == 1 => FrameType.Rendering,
            string stringValue when string.Equals(stringValue, "Rendering", StringComparison.OrdinalIgnoreCase) => FrameType.Rendering,
            _ => FrameType.Game,
        };
    }

    private bool TryResolveTimestamp(TraceEvent traceEvent, out double timestamp)
    {
        if (traceEvent.Timestamp.Seconds > 0)
        {
            timestamp = traceEvent.Timestamp.Seconds;
            return true;
        }

        if (TryGetUInt64(traceEvent, "Cycle", out var cycle) && traceEvent.Timestamp.SecondsPerCycle.HasValue)
        {
            timestamp = cycle * traceEvent.Timestamp.SecondsPerCycle.Value;
            return true;
        }

        if (!traceEvent.Timestamp.SecondsPerCycle.HasValue || traceEvent.Attachment.IsEmpty)
        {
            timestamp = 0;
            return false;
        }

        var frameType = traceEvent.Descriptor.EventName is "BeginRenderFrame" or "EndRenderFrame"
            ? FrameType.Rendering
            : FrameType.Game;

        var offset = 0;
        var cycleDiff = Decode7Bit(traceEvent.Attachment.Span, ref offset);
        var frameIndex = frameType == FrameType.Rendering ? 1 : 0;
        var cycleValue = _lastFrameCycles[frameIndex] + cycleDiff;
        _lastFrameCycles[frameIndex] = cycleValue;
        timestamp = cycleValue * traceEvent.Timestamp.SecondsPerCycle.Value;
        return true;
    }

    private static bool TryGetUInt64(TraceEvent traceEvent, string fieldName, out ulong value)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var fieldValue) || fieldValue is null)
        {
            value = 0;
            return false;
        }

        switch (fieldValue)
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

        return value;
    }
}
