using TraceViewer.SessionModel;

namespace TraceViewer.Query;

public sealed class CpuCallTreeBuilder
{
    private readonly record struct AggregationKey(string Name, TimerRef TimerRef);

    private sealed class AggregationBucket
    {
        public required IReadOnlyList<CallTreeNode> Nodes { get; init; }

        public IReadOnlyList<CallTreeNode> AggregatedChildren { get; set; } = Array.Empty<CallTreeNode>();

        public bool ChildrenProcessed { get; set; }
    }

    private sealed class AggregationFrame
    {
        public AggregationFrame(IReadOnlyList<AggregationBucket> buckets, AggregationBucket? ownerBucket)
        {
            Buckets = buckets;
            OwnerBucket = ownerBucket;
        }

        public IReadOnlyList<AggregationBucket> Buckets { get; }

        public AggregationBucket? OwnerBucket { get; }

        public int NextBucketIndex { get; set; }

        public List<CallTreeNode> Result { get; } = [];
    }

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

        var stack = new Stack<AggregationFrame>();
        stack.Push(new AggregationFrame(GroupNodes(nodes), ownerBucket: null));

        while (stack.Count > 0)
        {
            var frame = stack.Peek();
            if (frame.NextBucketIndex >= frame.Buckets.Count)
            {
                frame.Result.Sort((left, right) => right.InclusiveMilliseconds.CompareTo(left.InclusiveMilliseconds));
                stack.Pop();

                if (frame.OwnerBucket is not null)
                {
                    frame.OwnerBucket.AggregatedChildren = frame.Result;
                    continue;
                }

                return frame.Result;
            }

            var bucket = frame.Buckets[frame.NextBucketIndex];
            if (!bucket.ChildrenProcessed)
            {
                bucket.ChildrenProcessed = true;
                var childNodes = bucket.Nodes.SelectMany(node => node.Children).ToArray();
                if (childNodes.Length > 0)
                {
                    stack.Push(new AggregationFrame(GroupNodes(childNodes), bucket));
                    continue;
                }
            }

            frame.Result.Add(CreateAggregatedNode(bucket));
            frame.NextBucketIndex++;
        }

        return Array.Empty<CallTreeNode>();
    }

    private static IReadOnlyList<AggregationBucket> GroupNodes(IReadOnlyList<CallTreeNode> nodes)
    {
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

        return grouped.Values
            .Select(bucket => new AggregationBucket { Nodes = bucket })
            .ToArray();
    }

    private static CallTreeNode CreateAggregatedNode(AggregationBucket bucket)
    {
        var nodes = bucket.Nodes;
        var first = nodes[0];
        var inclusiveMilliseconds = nodes.Sum(node => node.InclusiveMilliseconds);
        var exclusiveMilliseconds = nodes.Sum(node => node.ExclusiveMilliseconds);
        var childrenMilliseconds = nodes.Sum(node => node.ChildrenMilliseconds);

        return new CallTreeNode
        {
            Name = first.Name,
            TimerRef = first.TimerRef,
            StartTime = nodes.Min(node => node.StartTime),
            EndTime = nodes.Max(node => node.EndTime),
            InclusiveMilliseconds = inclusiveMilliseconds,
            ExclusiveMilliseconds = exclusiveMilliseconds,
            ChildrenMilliseconds = childrenMilliseconds,
            StartedBeforeFrame = nodes.Any(node => node.StartedBeforeFrame),
            EndedAfterFrame = nodes.Any(node => node.EndedAfterFrame),
            Children = bucket.AggregatedChildren,
        };
    }
}
