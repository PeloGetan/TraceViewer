using TraceViewer.Analysis;
using TraceViewer.SessionModel;
using TraceViewer.TraceFormat;

namespace TraceViewer.Tests;

public sealed class FramesModuleTests
{
    [Fact]
    public void Execute_BuildsGameAndRenderingFramesFromMiscEvents()
    {
        var events = new[]
        {
            TraceEventFactory.Create("Misc", "BeginFrame", 1.0, fields: new Dictionary<string, object?> { ["FrameType"] = 0 }),
            TraceEventFactory.Create("Misc", "EndFrame", 4.0, fields: new Dictionary<string, object?> { ["FrameType"] = 0 }),
            TraceEventFactory.Create("Misc", "BeginRenderFrame", 1.5),
            TraceEventFactory.Create("Misc", "EndRenderFrame", 3.5),
        };

        var pipeline = new AnalysisPipeline([new FramesModule()]);
        var session = pipeline.Execute(new TraceReadResult(events));

        var gameFrames = session.Frames.GetSeries(FrameType.Game).Frames;
        var renderingFrames = session.Frames.GetSeries(FrameType.Rendering).Frames;

        var gameFrame = Assert.Single(gameFrames);
        Assert.Equal(1.0, gameFrame.StartTime);
        Assert.Equal(4.0, gameFrame.EndTime);

        var renderingFrame = Assert.Single(renderingFrames);
        Assert.Equal(1.5, renderingFrame.StartTime);
        Assert.Equal(3.5, renderingFrame.EndTime);

        Assert.Equal(4.0, session.DurationSeconds);
    }
}
