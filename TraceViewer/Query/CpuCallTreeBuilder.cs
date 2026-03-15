using TraceViewer.SessionModel;

namespace TraceViewer.Query;

public sealed class CpuCallTreeBuilder
{
    private readonly record struct AggregationKey(string Name, TimerRef TimerRef);

    private sealed class ActiveScope
    {
        public required TimerRef TimerRef { get; init; }

        public required double ActualStartTime { get; init; }

        public List<CallTreeNode> Children { get; } = [];

        public double ChildrenVisibleMilliseconds { get; set; }
    }

    public IReadOnlyList<ThreadFrameView> BuildForFrame(TraceSession session, FrameInfo frame)
    {
        var activeThreads = new List<ThreadFrameView>();

        foreach (var timeline in session.CpuTimelines.Timelines)
        {
            var roots = BuildThreadRoots(session, timeline, frame);
            if (roots.Count == 0)
            {
                continue;
            }

            var thread = session.Threads.TryGetThread(timeline.ThreadId)
                ?? session.Threads.GetOrCreateThread(timeline.ThreadId);

            var totalInclusiveMilliseconds = roots.Sum(root => root.InclusiveMilliseconds);
            activeThreads.Add(new ThreadFrameView(thread, totalInclusiveMilliseconds, roots));
        }

        activeThreads.Sort((left, right) => right.TotalInclusiveMilliseconds.CompareTo(left.TotalInclusiveMilliseconds));
        return activeThreads;
    }

    private static IReadOnlyList<CallTreeNode> BuildThreadRoots(TraceSession session, CpuThreadTimeline timeline, FrameInfo frame)
    {
        var activeScopes = new Stack<ActiveScope>();
        var rootNodes = new List<CallTreeNode>();

        foreach (var timelineEvent in timeline.Events)
        {
            if (timelineEvent.Timestamp > frame.EndTime)
            {
                break;
            }

            if (timelineEvent.IsBegin)
            {
                activeScopes.Push(new ActiveScope
                {
                    TimerRef = timelineEvent.TimerRef,
                    ActualStartTime = timelineEvent.Timestamp,
                });
                continue;
            }

            if (activeScopes.Count == 0)
            {
                continue;
            }

            CloseScope(session, frame, timelineEvent.Timestamp, activeScopes, rootNodes, endedAfterFrame: false);
        }

        while (activeScopes.Count > 0)
        {
            CloseScope(session, frame, frame.EndTime, activeScopes, rootNodes, endedAfterFrame: true);
        }

        return AggregateAndSort(rootNodes);
    }

    private static void CloseScope(
        TraceSession session,
        FrameInfo frame,
        double actualEndTime,
        Stack<ActiveScope> activeScopes,
        List<CallTreeNode> rootNodes,
        bool endedAfterFrame)
    {
        var scope = activeScopes.Pop();
        var visibleStart = Math.Max(scope.ActualStartTime, frame.StartTime);
        var visibleEnd = Math.Min(actualEndTime, frame.EndTime);
        var visibleDurationMilliseconds = Math.Max(0.0, (visibleEnd - visibleStart) * 1000.0);

        if (visibleDurationMilliseconds <= 0.0)
        {
            return;
        }

        var timer = session.ResolveTimerDefinition(scope.TimerRef);
        var name = timer?.Name ?? "<unknown>";
        var childrenMilliseconds = scope.ChildrenVisibleMilliseconds;
        var exclusiveMilliseconds = Math.Max(0.0, visibleDurationMilliseconds - childrenMilliseconds);

        var node = new CallTreeNode
        {
            Name = name,
            TimerRef = scope.TimerRef,
            StartTime = visibleStart,
            EndTime = visibleEnd,
            InclusiveMilliseconds = visibleDurationMilliseconds,
            ExclusiveMilliseconds = exclusiveMilliseconds,
            ChildrenMilliseconds = childrenMilliseconds,
            StartedBeforeFrame = scope.ActualStartTime < frame.StartTime,
            EndedAfterFrame = endedAfterFrame,
            Children = scope.Children,
        };

        if (activeScopes.TryPeek(out var parent))
        {
            parent.Children.Add(node);
            parent.ChildrenVisibleMilliseconds += visibleDurationMilliseconds;
        }
        else
        {
            rootNodes.Add(node);
        }
    }

    private static IReadOnlyList<CallTreeNode> AggregateAndSort(IReadOnlyList<CallTreeNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return Array.Empty<CallTreeNode>();
        }

        var grouped = new Dictionary<AggregationKey, List<CallTreeNode>>();
        foreach (var node in nodes)
        {
            var key = new AggregationKey(node.Name, node.TimerRef);
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = [];
                grouped.Add(key, bucket);
            }

            bucket.Add(node);
        }

        var aggregatedNodes = new List<CallTreeNode>(grouped.Count);
        foreach (var pair in grouped)
        {
            var bucket = pair.Value;
            var first = bucket[0];
            var aggregatedChildren = AggregateAndSort(bucket.SelectMany(node => node.Children).ToArray());

            var inclusiveMilliseconds = bucket.Sum(node => node.InclusiveMilliseconds);
            var exclusiveMilliseconds = bucket.Sum(node => node.ExclusiveMilliseconds);
            var childrenMilliseconds = bucket.Sum(node => node.ChildrenMilliseconds);

            aggregatedNodes.Add(new CallTreeNode
            {
                Name = first.Name,
                TimerRef = first.TimerRef,
                StartTime = bucket.Min(node => node.StartTime),
                EndTime = bucket.Max(node => node.EndTime),
                InclusiveMilliseconds = inclusiveMilliseconds,
                ExclusiveMilliseconds = exclusiveMilliseconds,
                ChildrenMilliseconds = childrenMilliseconds,
                StartedBeforeFrame = bucket.Any(node => node.StartedBeforeFrame),
                EndedAfterFrame = bucket.Any(node => node.EndedAfterFrame),
                Children = aggregatedChildren,
            });
        }

        aggregatedNodes.Sort((left, right) => right.InclusiveMilliseconds.CompareTo(left.InclusiveMilliseconds));
        return aggregatedNodes;
    }
}
