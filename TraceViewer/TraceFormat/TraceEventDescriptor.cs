namespace TraceViewer.TraceFormat;

public readonly record struct TraceEventDescriptor(
    string Logger,
    string EventName,
    TraceEventScopePhase ScopePhase = TraceEventScopePhase.None);
