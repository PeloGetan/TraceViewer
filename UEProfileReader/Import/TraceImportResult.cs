using UEProfileReader.SessionModel;
using UEProfileReader.TraceFormat;

namespace UEProfileReader.Import;

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
