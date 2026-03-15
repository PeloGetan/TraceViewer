namespace UEProfileReader.SessionModel;

public readonly record struct TimelineEvent(double Timestamp, bool IsBegin, TimerRef TimerRef);
