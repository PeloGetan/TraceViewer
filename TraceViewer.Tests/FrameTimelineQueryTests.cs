using TraceViewer.Query;
using TraceViewer.SessionModel;

namespace TraceViewer.Tests;

public sealed class FrameTimelineQueryTests
{
    [Fact]
    public void GetBars_ReturnsRawFrameDurationsForViewportScaling()
    {
        var series = new FrameSeries(FrameType.Game);
        series.BeginFrame(0.0);
        series.EndFrame(100.0);
        series.BeginFrame(100.0);
        series.EndFrame(101.0);

        var query = new FrameTimelineQuery();
        var bars = query.GetBars(series, 0.0, 101.0);

        Assert.Equal(2, bars.Count);
        Assert.Equal(100.0, bars[0].DurationSeconds, 6);
        Assert.Equal(1.0, bars[1].DurationSeconds, 6);
    }
}
