namespace UEProfileReader.TraceFormat;

public readonly record struct TraceTimestamp(double Seconds, ulong? Cycle = null, double? SecondsPerCycle = null);
