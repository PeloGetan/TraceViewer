namespace UEProfileReader.SessionModel;

public sealed class FrameInfo
{
    public required ulong Index { get; init; }

    public required FrameType FrameType { get; init; }

    public required double StartTime { get; init; }

    public double EndTime { get; set; }
}
