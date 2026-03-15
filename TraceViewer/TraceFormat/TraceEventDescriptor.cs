namespace TraceViewer.TraceFormat;

public sealed record TraceEventDescriptor(
    string Logger,
    string EventName,
    TraceEventScopePhase ScopePhase = TraceEventScopePhase.None);
