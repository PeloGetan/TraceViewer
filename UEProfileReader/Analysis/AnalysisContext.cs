using UEProfileReader.SessionModel;

namespace UEProfileReader.Analysis;

public sealed class AnalysisContext : IAnalysisContext
{
    public AnalysisContext(TraceSession session)
    {
        Session = session;
    }

    public TraceSession Session { get; }
}
