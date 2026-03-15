using TraceViewer.SessionModel;

namespace TraceViewer.Analysis;

public interface IAnalysisContext
{
    TraceSession Session { get; }
}
