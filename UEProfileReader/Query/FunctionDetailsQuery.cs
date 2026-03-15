using UEProfileReader.SessionModel;

namespace UEProfileReader.Query;

public sealed class FunctionDetailsQuery
{
    public FunctionDetails Build(ThreadInfo thread, CallTreeNode node)
    {
        return new FunctionDetails(
            node.Name,
            thread.Name,
            node.InclusiveMilliseconds,
            node.ExclusiveMilliseconds,
            node.ChildrenMilliseconds,
            node.StartTime,
            node.EndTime,
            node.StartedBeforeFrame,
            node.EndedAfterFrame);
    }
}
