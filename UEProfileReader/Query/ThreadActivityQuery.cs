using UEProfileReader.SessionModel;

namespace UEProfileReader.Query;

public sealed class ThreadActivityQuery
{
    private readonly CpuCallTreeBuilder _callTreeBuilder;

    public ThreadActivityQuery(CpuCallTreeBuilder callTreeBuilder)
    {
        _callTreeBuilder = callTreeBuilder;
    }

    public IReadOnlyList<ThreadFrameView> GetActiveThreads(TraceSession session, FrameInfo frame)
    {
        return _callTreeBuilder.BuildForFrame(session, frame);
    }
}
