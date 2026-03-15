using TraceViewer.TraceFormat;

namespace TraceViewer.Analysis;

public interface ITraceAnalysisModule
{
    string Name { get; }

    void Initialize(IAnalysisContext context);

    bool CanHandle(in TraceEvent traceEvent);

    void Process(in TraceEvent traceEvent, IAnalysisContext context);

    void Complete(IAnalysisContext context);
}
