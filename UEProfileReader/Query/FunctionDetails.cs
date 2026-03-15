namespace UEProfileReader.Query;

public sealed record FunctionDetails(
    string Name,
    string Thread,
    double InclusiveMilliseconds,
    double ExclusiveMilliseconds,
    double ChildrenMilliseconds,
    double StartTime,
    double EndTime,
    bool StartedBeforeFrame,
    bool EndedAfterFrame);
