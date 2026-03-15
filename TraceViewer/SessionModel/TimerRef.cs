namespace TraceViewer.SessionModel;

public readonly record struct TimerRef(TimerRefKind Kind, int Id)
{
    public static TimerRef ForTimer(int timerId) => new(TimerRefKind.Timer, timerId);

    public static TimerRef ForMetadata(int metadataId) => new(TimerRefKind.Metadata, metadataId);
}
