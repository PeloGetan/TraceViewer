using TraceViewer.SessionModel;
using TraceViewer.TraceFormat;

namespace TraceViewer.Import;

public sealed class TraceImportResult
{
    public TraceImportResult(TraceReadResult readResult, TraceSession session)
    {
        ReadResult = readResult;
        Session = session;
    }

    public TraceReadResult ReadResult { get; }

    public TraceSession Session { get; }
}
