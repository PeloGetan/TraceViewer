using UEProfileReader.Analysis;
using UEProfileReader.TraceFormat;

namespace UEProfileReader.Import;

public sealed class TraceImportService
{
    private readonly ITraceReader _traceReader;

    public TraceImportService()
        : this(new UTraceFileReader())
    {
    }

    public TraceImportService(ITraceReader traceReader)
    {
        _traceReader = traceReader;
    }

    public TraceImportResult Import(string traceFilePath)
    {
        var readResult = _traceReader.Read(traceFilePath);
        var pipeline = new AnalysisPipeline(
        [
            new FramesModule(),
            new ThreadsModule(),
            new CpuProfilerModule(),
        ]);

        var session = pipeline.Execute(readResult);
        return new TraceImportResult(readResult, session);
    }
}
