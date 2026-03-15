namespace TraceViewer.SessionModel;

public sealed class CpuThreadTimeline
{
    private readonly List<TimelineEvent> _events = [];

    public CpuThreadTimeline(uint threadId)
    {
        ThreadId = threadId;
    }

    public uint ThreadId { get; }

    public IReadOnlyList<TimelineEvent> Events => _events;

    public double LastTimestamp => _events.Count > 0 ? _events[^1].Timestamp : 0.0;

    public void AddEvent(TimelineEvent timelineEvent)
    {
        if (_events.Count > 0 && timelineEvent.Timestamp < _events[^1].Timestamp)
        {
            throw new InvalidOperationException("Timeline events must be appended in timestamp order.");
        }

        _events.Add(timelineEvent);
    }
}
