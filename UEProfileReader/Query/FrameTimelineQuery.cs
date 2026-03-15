using UEProfileReader.SessionModel;

namespace UEProfileReader.Query;

public sealed class FrameTimelineQuery
{
    public IReadOnlyList<FrameTimelineBar> GetBars(FrameSeries frameSeries, double startTime, double endTime)
    {
        var visibleFrames = frameSeries.EnumerateIntersecting(startTime, endTime);
        if (visibleFrames.Count == 0)
        {
            return Array.Empty<FrameTimelineBar>();
        }

        var maxDuration = visibleFrames
            .Select(frame => Math.Max(0.0, frame.EndTime - frame.StartTime))
            .DefaultIfEmpty(1.0)
            .Max();

        if (maxDuration <= 0.0)
        {
            maxDuration = 1.0;
        }

        return visibleFrames
            .Select(frame => new FrameTimelineBar(frame, Math.Max(0.05, (frame.EndTime - frame.StartTime) / maxDuration)))
            .ToArray();
    }
}
