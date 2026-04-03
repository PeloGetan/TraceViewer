using TraceViewer.SessionModel;

namespace TraceViewer.Query;

public sealed class FrameTimelineQuery
{
    public IReadOnlyList<FrameTimelineBar> GetBars(FrameSeries frameSeries, double startTime, double endTime)
    {
        var visibleFrames = frameSeries.EnumerateIntersecting(startTime, endTime);
        if (visibleFrames.Count == 0)
        {
            return Array.Empty<FrameTimelineBar>();
        }

        return visibleFrames
            .Select(frame => new FrameTimelineBar(frame, Math.Max(0.0, frame.EndTime - frame.StartTime)))
            .ToArray();
    }
}
