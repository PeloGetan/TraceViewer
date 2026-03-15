using TraceViewer.SessionModel;

namespace TraceViewer.Analysis;

public sealed class AnalysisContext : IAnalysisContext
{
    public AnalysisContext(TraceSession session)
    {
        Session = session;
    }

    public TraceSession Session { get; }
}
