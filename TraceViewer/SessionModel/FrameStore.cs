namespace TraceViewer.SessionModel;

public sealed class FrameStore
{
    private readonly Dictionary<FrameType, FrameSeries> _series = new()
    {
        [FrameType.Game] = new FrameSeries(FrameType.Game),
        [FrameType.Rendering] = new FrameSeries(FrameType.Rendering),
    };

    public FrameSeries GetSeries(FrameType frameType) => _series[frameType];

    public FrameInfo BeginFrame(FrameType frameType, double time) => _series[frameType].BeginFrame(time);

    public void EndFrame(FrameType frameType, double time) => _series[frameType].EndFrame(time);
}
