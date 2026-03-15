using TraceViewer.SessionModel;
using TraceViewer.TraceFormat;

namespace TraceViewer.Analysis;

public sealed class AnalysisPipeline
{
    private readonly IReadOnlyList<ITraceAnalysisModule> _modules;

    public AnalysisPipeline(IEnumerable<ITraceAnalysisModule> modules)
    {
        _modules = modules.ToArray();
    }

    public TraceSession Execute(TraceReadResult readResult)
    {
        var session = new TraceSession();
        var context = new AnalysisContext(session);

        foreach (var module in _modules)
        {
            module.Initialize(context);
        }

        foreach (var traceEvent in readResult.Events)
        {
            foreach (var module in _modules)
            {
                if (module.CanHandle(traceEvent))
                {
                    module.Process(traceEvent, context);
                }
            }
        }

        foreach (var module in _modules)
        {
            module.Complete(context);
        }

        return session;
    }
}
