using TraceViewer.Analysis;
using TraceViewer.TraceFormat;

namespace TraceViewer.Import;

public sealed class TraceImportService
{
    private readonly ITraceReader _traceReader;
    private readonly bool _retainRawTraceData;

    public TraceImportService()
        : this(new UTraceFileReader(), retainRawTraceData: false)
    {
    }

    public TraceImportService(bool retainRawTraceData)
        : this(new UTraceFileReader(), retainRawTraceData)
    {
    }

    public TraceImportService(ITraceReader traceReader, bool retainRawTraceData = false)
    {
        _traceReader = traceReader;
        _retainRawTraceData = retainRawTraceData;
    }

    public TraceImportResult Import(string traceFilePath)
    {
        var pipeline = CreatePipeline();

        if (!_retainRawTraceData && _traceReader is UTraceFileReader traceReader)
        {
            TraceReadResult? streamedReadResult = null;
            var streamedSession = pipeline.Execute(emit =>
            {
                streamedReadResult = traceReader.Read(
                    traceFilePath,
                    emit,
                    retainEvents: false,
                    retainPackets: false);
            });

            return new TraceImportResult(streamedReadResult!, streamedSession);
        }

        var readResult = _traceReader.Read(traceFilePath);
        var session = pipeline.Execute(readResult);
        if (!_retainRawTraceData)
        {
            readResult = new TraceReadResult(
                Array.Empty<TraceEvent>(),
                readResult.FileHeader,
                eventCount: readResult.EventCount,
                packetCount: readResult.PacketCount);
        }

        return new TraceImportResult(readResult, session);
    }

    private static AnalysisPipeline CreatePipeline()
    {
        return new AnalysisPipeline(
        [
            new FramesModule(),
            new ThreadsModule(),
            new CpuProfilerModule(),
        ]);
    }
}
