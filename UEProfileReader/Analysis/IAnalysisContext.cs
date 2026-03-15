using UEProfileReader.SessionModel;

namespace UEProfileReader.Analysis;

public interface IAnalysisContext
{
    TraceSession Session { get; }
}
