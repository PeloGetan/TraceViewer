namespace TraceViewer.SessionModel;

public sealed class ThreadInfo
{
    public required uint Id { get; init; }

    public string Name { get; set; } = "UnnamedThread";

    public string? GroupName { get; set; }

    public ProfilerThreadPriority Priority { get; set; } = ProfilerThreadPriority.Unknown;

    public uint FallbackOrder { get; init; }

    public ThreadSortKey SortKey { get; set; }
}
