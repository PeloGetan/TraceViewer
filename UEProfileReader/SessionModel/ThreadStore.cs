namespace UEProfileReader.SessionModel;

public sealed class ThreadStore
{
    private static readonly IReadOnlyDictionary<string, uint> KnownGroupRanks = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["Render"] = 0,
        ["AsyncLoading"] = 1,
        ["TaskGraphHigh"] = 2,
        ["TaskGraphNormal"] = 3,
        ["TaskGraphLow"] = 4,
        ["LargeThreadPool"] = 5,
        ["ThreadPool"] = 6,
        ["BackgroundThreadPool"] = 7,
        ["IOThreadPool"] = 8,
    };

    private readonly Dictionary<uint, ThreadInfo> _byId = [];
    private readonly List<ThreadInfo> _sortedThreads = [];

    public ulong ModCount { get; private set; }

    public ThreadInfo AddOrUpdateThread(uint id, string? name, ProfilerThreadPriority priority)
    {
        if (!_byId.TryGetValue(id, out var thread))
        {
            thread = new ThreadInfo
            {
                Id = id,
                FallbackOrder = (uint)_sortedThreads.Count,
                SortKey = default,
            };

            _byId.Add(id, thread);
            _sortedThreads.Add(thread);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            thread.Name = name;
        }

        thread.Priority = priority;

        if (string.Equals(thread.Name, "RHIThread", StringComparison.Ordinal))
        {
            thread.GroupName = "Render";
        }

        thread.SortKey = BuildSortKey(thread);
        SortThreads();
        ModCount++;
        return thread;
    }

    public ThreadInfo AddGameThread(uint id)
    {
        return AddOrUpdateThread(id, "GameThread", ProfilerThreadPriority.GameThread);
    }

    public void SetThreadName(uint id, string? name)
    {
        var priority = _byId.TryGetValue(id, out var existing) ? existing.Priority : ProfilerThreadPriority.Unknown;
        AddOrUpdateThread(id, name, priority);
    }

    public void SetThreadGroup(uint id, string? groupName)
    {
        var thread = GetOrCreateThread(id);
        thread.GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName;
        thread.SortKey = BuildSortKey(thread);
        SortThreads();
        ModCount++;
    }

    public ThreadInfo GetOrCreateThread(uint id)
    {
        return _byId.TryGetValue(id, out var thread)
            ? thread
            : AddOrUpdateThread(id, null, ProfilerThreadPriority.Unknown);
    }

    public ThreadInfo? TryGetThread(uint id)
    {
        return _byId.GetValueOrDefault(id);
    }

    public string GetThreadName(uint id)
    {
        return _byId.TryGetValue(id, out var thread) ? thread.Name : string.Empty;
    }

    public IReadOnlyList<ThreadInfo> GetOrderedThreads()
    {
        return _sortedThreads;
    }

    private void SortThreads()
    {
        _sortedThreads.Sort((left, right) => left.SortKey.CompareTo(right.SortKey));
    }

    private static ThreadSortKey BuildSortKey(ThreadInfo thread)
    {
        var specialPriorityRank = (int)thread.Priority < 0 ? (int)thread.Priority : int.MaxValue;
        var priorityRank = (int)thread.Priority < 0 ? int.MaxValue : (int)thread.Priority;
        var groupRank = GetGroupRank(thread.GroupName);

        return new ThreadSortKey(specialPriorityRank, groupRank, priorityRank, thread.FallbackOrder);
    }

    private static uint GetGroupRank(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return uint.MaxValue;
        }

        return KnownGroupRanks.TryGetValue(groupName, out var rank)
            ? rank
            : unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(groupName));
    }
}
