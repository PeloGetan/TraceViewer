using TraceViewer.SessionModel;
using TraceViewer.TraceFormat;

namespace TraceViewer.Analysis;

public sealed class ThreadsModule : ITraceAnalysisModule
{
    private const uint MainThreadId = 2;

    private static readonly HashSet<string> SupportedEvents =
    [
        "ThreadInfo",
        "ThreadGroupBegin",
        "ThreadGroupEnd",
        "RegisterGameThread",
        "CreateThread",
        "SetThreadGroup",
        "BeginThreadGroupScope",
        "EndThreadGroupScope",
    ];

    private readonly Dictionary<uint, Stack<string>> _threadGroupScopes = [];
    public string Name => "Threads";

    public void Initialize(IAnalysisContext context)
    {
        _threadGroupScopes.Clear();
    }

    public bool CanHandle(in TraceEvent traceEvent)
    {
        return SupportedEvents.Contains(traceEvent.Descriptor.EventName) &&
               traceEvent.Descriptor.Logger is "Misc" or "$Trace" or "Trace";
    }

    public void Process(in TraceEvent traceEvent, IAnalysisContext context)
    {
        var threads = context.Session.Threads;

        switch (traceEvent.Descriptor.EventName)
        {
            case "ThreadInfo":
            {
                var threadId = GetUInt32(traceEvent, "ThreadId", traceEvent.ThreadId);
                var thread = threads.AddOrUpdateThread(
                    threadId,
                    GetString(traceEvent, "Name"),
                    ReadPriority(traceEvent, "SortHint"));

                var groupName = GetString(traceEvent, "GroupName");
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    threads.SetThreadGroup(thread.Id, groupName);
                }

                break;
            }

            case "RegisterGameThread":
                threads.AddGameThread(traceEvent.ThreadId);
                break;

            case "ThreadGroupBegin":
            {
                var groupName = GetString(traceEvent, "Name");
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    break;
                }

                if (!_threadGroupScopes.TryGetValue(traceEvent.ThreadId, out var groupStack))
                {
                    groupStack = new Stack<string>();
                    _threadGroupScopes.Add(traceEvent.ThreadId, groupStack);
                }

                groupStack.Push(groupName);
                break;
            }

            case "ThreadGroupEnd":
            {
                if (_threadGroupScopes.TryGetValue(traceEvent.ThreadId, out var groupStack) && groupStack.Count > 0)
                {
                    groupStack.Pop();
                }

                break;
            }

            case "CreateThread":
            {
                var createdThreadId = GetUInt32(traceEvent, "CreatedThreadId", traceEvent.ThreadId);
                var currentThreadId = GetUInt32(traceEvent, "CurrentThreadId", traceEvent.ThreadId);
                var createdThread = threads.AddOrUpdateThread(
                    createdThreadId,
                    GetString(traceEvent, "Name") ?? DecodeWideAttachmentString(traceEvent.Attachment),
                    ReadPriority(traceEvent, "Priority"));

                if (_threadGroupScopes.TryGetValue(currentThreadId, out var groupScope) && groupScope.Count > 0)
                {
                    threads.SetThreadGroup(createdThread.Id, groupScope.Peek());
                }

                break;
            }

            case "SetThreadGroup":
            {
                var threadId = GetUInt32(traceEvent, "ThreadId", traceEvent.ThreadId);
                threads.SetThreadGroup(threadId, GetString(traceEvent, "GroupName") ?? DecodeAnsiAttachmentString(traceEvent.Attachment));
                break;
            }

            case "BeginThreadGroupScope":
            {
                var currentThreadId = GetUInt32(traceEvent, "CurrentThreadId", traceEvent.ThreadId);
                var groupName = GetString(traceEvent, "GroupName") ?? DecodeAnsiAttachmentString(traceEvent.Attachment);
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    break;
                }

                if (!_threadGroupScopes.TryGetValue(currentThreadId, out var stack))
                {
                    stack = new Stack<string>();
                    _threadGroupScopes.Add(currentThreadId, stack);
                }

                stack.Push(groupName);
                break;
            }

            case "EndThreadGroupScope":
            {
                var currentThreadId = GetUInt32(traceEvent, "CurrentThreadId", traceEvent.ThreadId);
                if (_threadGroupScopes.TryGetValue(currentThreadId, out var stack) && stack.Count > 0)
                {
                    stack.Pop();
                }

                break;
            }
        }
    }

    public void Complete(IAnalysisContext context)
    {
        var threads = context.Session.Threads.GetOrderedThreads();
        if (threads.Any(thread => string.Equals(thread.Name, "GameThread", StringComparison.Ordinal)))
        {
            return;
        }

        var inferredGameThread = context.Session.Threads.TryGetThread(MainThreadId);
        if (inferredGameThread is null || !string.Equals(inferredGameThread.Name, "UnnamedThread", StringComparison.Ordinal))
        {
            return;
        }

        context.Session.Threads.AddGameThread(MainThreadId);
    }

    private static ProfilerThreadPriority ReadPriority(TraceEvent traceEvent)
    {
        return ReadPriority(traceEvent, "Priority");
    }

    private static ProfilerThreadPriority ReadPriority(TraceEvent traceEvent, string fieldName)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return ProfilerThreadPriority.Unknown;
        }

        return value switch
        {
            ProfilerThreadPriority priority => priority,
            int intValue => (ProfilerThreadPriority)intValue,
            uint uintValue => (ProfilerThreadPriority)(int)uintValue,
            _ => ProfilerThreadPriority.Unknown,
        };
    }

    private static string? GetString(TraceEvent traceEvent, string fieldName)
    {
        return traceEvent.Fields.TryGetValue(fieldName, out var value) ? value as string : null;
    }

    private static string? DecodeAnsiAttachmentString(ReadOnlyMemory<byte> attachment)
    {
        if (attachment.IsEmpty)
        {
            return null;
        }

        return System.Text.Encoding.UTF8.GetString(attachment.Span).TrimEnd('\0');
    }

    private static string? DecodeWideAttachmentString(ReadOnlyMemory<byte> attachment)
    {
        if (attachment.IsEmpty)
        {
            return null;
        }

        return System.Text.Encoding.Unicode.GetString(attachment.Span).TrimEnd('\0');
    }

    private static uint GetUInt32(TraceEvent traceEvent, string fieldName, uint fallback)
    {
        if (!traceEvent.Fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            uint uintValue => uintValue,
            int intValue => (uint)intValue,
            long longValue => (uint)longValue,
            _ => fallback,
        };
    }
}
