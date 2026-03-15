using TraceViewer.SessionModel;

namespace TraceViewer.Query;

public sealed record ThreadFrameView(
    ThreadInfo Thread,
    double TotalInclusiveMilliseconds,
    IReadOnlyList<CallTreeNode> Roots);
