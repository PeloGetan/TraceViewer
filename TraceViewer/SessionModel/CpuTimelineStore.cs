namespace TraceViewer.SessionModel;

public sealed class CpuTimelineStore
{
    private readonly Dictionary<uint, CpuThreadTimeline> _timelines = [];

    public IReadOnlyCollection<CpuThreadTimeline> Timelines => _timelines.Values;

    public CpuThreadTimeline GetOrCreateTimeline(uint threadId)
    {
        if (_timelines.TryGetValue(threadId, out var timeline))
        {
            return timeline;
        }

        timeline = new CpuThreadTimeline(threadId);
        _timelines.Add(threadId, timeline);
        return timeline;
    }

    public CpuThreadTimeline? TryGetTimeline(uint threadId)
    {
        return _timelines.GetValueOrDefault(threadId);
    }
}
