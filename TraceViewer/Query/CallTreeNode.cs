using TraceViewer.SessionModel;

namespace TraceViewer.Query;

public sealed class CallTreeNode
{
    public required string Name { get; init; }

    public required TimerRef TimerRef { get; init; }

    public required double StartTime { get; init; }

    public required double EndTime { get; init; }

    public required double InclusiveMilliseconds { get; init; }

    public required double ExclusiveMilliseconds { get; init; }

    public required double ChildrenMilliseconds { get; init; }

    public required bool StartedBeforeFrame { get; init; }

    public required bool EndedAfterFrame { get; init; }

    public IReadOnlyList<CallTreeNode> Children { get; init; } = Array.Empty<CallTreeNode>();
}
