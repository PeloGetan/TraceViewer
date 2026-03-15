namespace UEProfileReader.TraceFormat;

public interface ITraceReader
{
    TraceReadResult Read(string traceFilePath);
}
