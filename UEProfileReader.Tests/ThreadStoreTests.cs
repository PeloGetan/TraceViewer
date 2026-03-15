using UEProfileReader.SessionModel;

namespace UEProfileReader.Tests;

public sealed class ThreadStoreTests
{
    [Fact]
    public void AddOrUpdateThread_AssignsRenderGroupToRhiThread()
    {
        var store = new ThreadStore();

        var thread = store.AddOrUpdateThread(7, "RHIThread", ProfilerThreadPriority.Normal);

        Assert.Equal("Render", thread.GroupName);
    }

    [Fact]
    public void GetOrderedThreads_UsesKnownGroupOrderAndFallbackOrder()
    {
        var store = new ThreadStore();
        store.AddOrUpdateThread(11, "WorkerA", ProfilerThreadPriority.Normal);
        store.SetThreadGroup(11, "ThreadPool");

        store.AddOrUpdateThread(12, "RenderA", ProfilerThreadPriority.Normal);
        store.SetThreadGroup(12, "Render");

        store.AddGameThread(1);

        var ordered = store.GetOrderedThreads();

        Assert.Equal(1u, ordered[0].Id);
        Assert.Equal(12u, ordered[1].Id);
        Assert.Equal(11u, ordered[2].Id);
    }
}
