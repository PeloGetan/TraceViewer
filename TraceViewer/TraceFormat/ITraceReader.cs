namespace TraceViewer.TraceFormat;

public interface ITraceReader
{
    TraceReadResult Read(string traceFilePath);
}
