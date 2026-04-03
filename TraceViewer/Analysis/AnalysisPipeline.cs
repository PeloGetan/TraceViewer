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
        return Execute(emit =>
        {
            foreach (var traceEvent in readResult.Events)
            {
                emit(traceEvent);
            }
        });
    }

    public TraceSession Execute(Action<Action<TraceEvent>> replay)
    {
        var session = new TraceSession();
        var context = new AnalysisContext(session);

        foreach (var module in _modules)
        {
            module.Initialize(context);
        }

        replay(traceEvent =>
        {
            foreach (var module in _modules)
            {
                if (module.CanHandle(traceEvent))
                {
                    module.Process(traceEvent, context);
                }
            }
        });

        foreach (var module in _modules)
        {
            module.Complete(context);
        }

        return session;
    }

}
