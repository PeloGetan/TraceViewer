using UEProfileReader.SessionModel;

namespace UEProfileReader.Query;

public sealed record ThreadFrameView(
    ThreadInfo Thread,
    double TotalInclusiveMilliseconds,
    IReadOnlyList<CallTreeNode> Roots);
