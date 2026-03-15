namespace UEProfileReader.SessionModel;

public sealed class TraceSession
{
    public FrameStore Frames { get; } = new();

    public ThreadStore Threads { get; } = new();

    public TimerRegistry Timers { get; } = new();

    public MetadataStore Metadata { get; } = new();

    public CpuTimelineStore CpuTimelines { get; } = new();

    public double DurationSeconds { get; private set; }

    public void UpdateDuration(double seconds)
    {
        DurationSeconds = Math.Max(DurationSeconds, seconds);
    }

    public TimerDefinition? ResolveTimerDefinition(TimerRef timerRef)
    {
        return timerRef.Kind switch
        {
            TimerRefKind.Timer => Timers.GetTimer(timerRef.Id),
            TimerRefKind.Metadata => Metadata.GetInstance(timerRef.Id) is { } metadata
                ? Timers.GetTimer(metadata.OriginalTimerId)
                : null,
            _ => null,
        };
    }
}
