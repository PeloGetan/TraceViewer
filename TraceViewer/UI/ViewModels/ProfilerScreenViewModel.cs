using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using TraceViewer.Import;
using TraceViewer.Query;
using TraceViewer.SessionModel;

namespace TraceViewer.UI.ViewModels;

public sealed class ProfilerScreenViewModel : ViewModelBase
{
    public const double MinFrameBarWidth = 4.0;
    public const double MaxFrameBarWidth = 36.0;
    public const double DefaultFrameBarWidth = 8.0;

    private readonly TraceImportService _traceImportService;
    private readonly FrameTimelineQuery _frameTimelineQuery;
    private readonly ThreadActivityQuery _threadActivityQuery;
    private readonly FunctionDetailsQuery _detailsQuery;
    private readonly List<ThreadTreeItemViewModel> _selectedThreadItems = [];
    private ThreadTreeItemViewModel? _selectionAnchor;
    private TraceImportResult? _importResult;
    private string _statusText;
    private double _frameBarWidth = DefaultFrameBarWidth;
    private bool _isLoading;

    public ProfilerScreenViewModel()
        : this(
            new TraceImportService(),
            new FrameTimelineQuery(),
            new ThreadActivityQuery(new CpuCallTreeBuilder()),
            new FunctionDetailsQuery())
    {
    }

    public ProfilerScreenViewModel(
        TraceImportService traceImportService,
        FrameTimelineQuery frameTimelineQuery,
        ThreadActivityQuery threadActivityQuery,
        FunctionDetailsQuery detailsQuery)
    {
        _traceImportService = traceImportService;
        _frameTimelineQuery = frameTimelineQuery;
        _threadActivityQuery = threadActivityQuery;
        _detailsQuery = detailsQuery;
        _statusText = "Open a local .utrace to populate frames, active threads, and function details.";
        ResetDetails();
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<FrameBarViewModel> FrameBars { get; } = [];

    public ObservableCollection<ThreadTreeItemViewModel> ThreadItems { get; } = [];

    public ObservableCollection<FunctionDetailItemViewModel> Details { get; } = [];

    public double FrameBarWidth
    {
        get => _frameBarWidth;
        set
        {
            var clamped = Math.Clamp(value, MinFrameBarWidth, MaxFrameBarWidth);
            if (Math.Abs(_frameBarWidth - clamped) < 0.001)
            {
                return;
            }

            _frameBarWidth = clamped;
            OnPropertyChanged();
        }
    }

    public bool HasFrameBars => FrameBars.Count > 0;

    public bool HasThreadItems => ThreadItems.Count > 0;

    public bool HasSelectedNode { get; private set; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInteractive));
        }
    }

    public bool IsInteractive => !IsLoading;

    public void AdjustFrameBarWidth(double delta)
    {
        FrameBarWidth += delta;
    }

    public async Task<bool> TryLoadDefaultTraceAsync()
    {
        var tracePath = FindDefaultTracePath();
        if (tracePath is null)
        {
            StatusText = "No local .utrace found yet. Use Open Trace to load a file.";
            return false;
        }

        await LoadTraceAsync(tracePath);
        return true;
    }

    public async Task LoadTraceAsync(string traceFilePath)
    {
        IsLoading = true;
        StatusText = $"Loading {Path.GetFileName(traceFilePath)}...";

        try
        {
            var result = await Task.Run(() => _traceImportService.Import(traceFilePath));
            ApplyImportResult(result, traceFilePath);
        }
        catch (Exception exception)
        {
            StatusText = $"Failed to load {Path.GetFileName(traceFilePath)}: {exception.Message}";
            ClearTraceView();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplyImportResult(TraceImportResult result, string sourceName)
    {
        _importResult = result;
        RebuildFrameBars(result.Session);

        var completedFrames = result.Session.Frames
            .GetSeries(FrameType.Game)
            .Frames
            .Where(frame => double.IsFinite(frame.EndTime))
            .ToArray();

        if (completedFrames.Length == 0)
        {
            StatusText = $"Loaded {Path.GetFileName(sourceName)}, but no completed Game frames were found.";
            RefreshThreadItems(Array.Empty<ThreadFrameView>());
            ResetDetails();
            return;
        }

        var selectedFrame = FindDefaultFrame(result.Session, completedFrames) ?? completedFrames[0];
        SelectFrame(selectedFrame);

        var threadCount = result.Session.Threads.GetOrderedThreads().Count;
        StatusText = $"Loaded {Path.GetFileName(sourceName)}: {completedFrames.Length} Game frames, {threadCount} threads, {result.Session.CpuTimelines.Timelines.Count} CPU timelines.";
    }

    public void SelectFrame(FrameInfo frame)
    {
        if (_importResult is null)
        {
            return;
        }

        foreach (var bar in FrameBars)
        {
            bar.IsSelected = ReferenceEquals(bar.Frame, frame);
        }

        var activeThreads = _threadActivityQuery.GetActiveThreads(_importResult.Session, frame);
        RefreshThreadItems(activeThreads);
        SetPrimaryTreeItem(ThreadItems.FirstOrDefault()?.Children.FirstOrDefault());
    }

    public void SelectFrame(FrameBarViewModel? frameBar)
    {
        if (frameBar is null)
        {
            return;
        }

        SelectFrame(frameBar.Frame);
    }

    public void SetPrimaryTreeItem(ThreadTreeItemViewModel? item)
    {
        ClearSelectedTreeItems();
        if (item is not null)
        {
            item.IsSelected = true;
            _selectedThreadItems.Add(item);
            _selectionAnchor = item;
        }

        SelectTreeItem(item);
    }

    public void ToggleTreeItemSelection(ThreadTreeItemViewModel? item, bool appendSelection)
    {
        if (item is null)
        {
            return;
        }

        if (!appendSelection)
        {
            SetPrimaryTreeItem(item);
            return;
        }

        if (_selectionAnchor is null)
        {
            SetPrimaryTreeItem(item);
            return;
        }

        var selectableItems = EnumerateSelectableTreeItems().ToArray();
        var anchorIndex = Array.IndexOf(selectableItems, _selectionAnchor);
        var targetIndex = Array.IndexOf(selectableItems, item);

        if (anchorIndex < 0 || targetIndex < 0)
        {
            SetPrimaryTreeItem(item);
            return;
        }

        ClearSelectedTreeItems();
        var startIndex = Math.Min(anchorIndex, targetIndex);
        var endIndex = Math.Max(anchorIndex, targetIndex);
        for (var index = startIndex; index <= endIndex; index++)
        {
            selectableItems[index].IsSelected = true;
            _selectedThreadItems.Add(selectableItems[index]);
        }

        SelectTreeItem(item);
    }

    public void SetExpandedRecursively(ThreadTreeItemViewModel? item, bool isExpanded)
    {
        if (item is null)
        {
            return;
        }

        item.IsExpanded = isExpanded;
        foreach (var child in item.Children)
        {
            SetExpandedRecursively(child, isExpanded);
        }
    }

    public void ExpandToFirstBranching(ThreadTreeItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsExpanded = true;
        if (item.Children.Count == 1)
        {
            ExpandToFirstBranching(item.Children[0]);
        }
    }

    public string BuildSelectedNamesClipboardText()
    {
        if (_selectedThreadItems.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            _selectedThreadItems
                .Select(item => item.DisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal));
    }

    public void SelectTreeItem(ThreadTreeItemViewModel? item)
    {
        if (item?.Thread is null || item.Node is null)
        {
            HasSelectedNode = false;
            OnPropertyChanged(nameof(HasSelectedNode));
            ResetDetails();
            return;
        }

        HasSelectedNode = true;
        OnPropertyChanged(nameof(HasSelectedNode));

        var details = _detailsQuery.Build(item.Thread, item.Node);
        var detailItems = new List<FunctionDetailItemViewModel>
        {
            CreateDetail("Name", details.Name),
            CreateDetail("Thread", details.Thread),
            CreateDetail("Inclusive ms", details.InclusiveMilliseconds.ToString("F3", CultureInfo.InvariantCulture)),
            CreateDetail("Exclusive ms", details.ExclusiveMilliseconds.ToString("F3", CultureInfo.InvariantCulture)),
            CreateDetail("Children ms", details.ChildrenMilliseconds.ToString("F3", CultureInfo.InvariantCulture)),
            CreateDetail("Start", details.StartTime.ToString("F6", CultureInfo.InvariantCulture)),
            CreateDetail("End", details.EndTime.ToString("F6", CultureInfo.InvariantCulture)),
            CreateDetail("StartedBeforeFrame", details.StartedBeforeFrame ? "true" : "false"),
            CreateDetail("EndedAfterFrame", details.EndedAfterFrame ? "true" : "false"),
        };

        if (item.HiddenChainNames.Count > 0)
        {
            detailItems.Add(CreateDetail("Hidden chain", string.Join(" -> ", item.HiddenChainNames)));
        }

        ReplaceDetails(detailItems);
    }

    private void RebuildFrameBars(TraceSession session)
    {
        FrameBars.Clear();

        var gameSeries = session.Frames.GetSeries(FrameType.Game);
        var completedFrames = gameSeries.Frames.Where(frame => double.IsFinite(frame.EndTime)).ToArray();
        if (completedFrames.Length == 0)
        {
            OnPropertyChanged(nameof(HasFrameBars));
            return;
        }

        var startTime = completedFrames[0].StartTime;
        var endTime = completedFrames[^1].EndTime;
        foreach (var bar in _frameTimelineQuery.GetBars(gameSeries, startTime, endTime))
        {
            FrameBars.Add(new FrameBarViewModel
            {
                Frame = bar.Frame,
                Height = Math.Max(10.0, bar.HeightRatio * 150.0),
                Tooltip = $"Frame {bar.Frame.Index}: {((bar.Frame.EndTime - bar.Frame.StartTime) * 1000.0).ToString("F3", CultureInfo.InvariantCulture)} ms",
            });
        }

        OnPropertyChanged(nameof(HasFrameBars));
    }

    private FrameInfo? FindDefaultFrame(TraceSession session, IReadOnlyList<FrameInfo> completedFrames)
    {
        ThreadFrameView[]? bestThreads = null;
        FrameInfo? bestFrame = null;

        foreach (var index in EnumerateCandidateIndices(completedFrames.Count))
        {
            var frame = completedFrames[index];
            var threads = _threadActivityQuery.GetActiveThreads(session, frame).ToArray();
            if (threads.Length == 0)
            {
                continue;
            }

            if (bestThreads is null ||
                threads.Length > bestThreads.Length ||
                (threads.Length == bestThreads.Length &&
                 threads.Sum(thread => thread.TotalInclusiveMilliseconds) > bestThreads.Sum(thread => thread.TotalInclusiveMilliseconds)))
            {
                bestThreads = threads;
                bestFrame = frame;
            }
        }

        return bestFrame;
    }

    private void RefreshThreadItems(IReadOnlyList<ThreadFrameView> activeThreads)
    {
        ClearSelectedTreeItems();
        _selectionAnchor = null;
        ThreadItems.Clear();
        foreach (var thread in activeThreads)
        {
            var threadItem = new ThreadTreeItemViewModel
            {
                Thread = thread.Thread,
                DisplayName = thread.Thread.Name,
                DurationText = $"{thread.TotalInclusiveMilliseconds:F3} ms",
                IsExpanded = true,
            };

            foreach (var root in thread.Roots)
            {
                threadItem.Children.Add(BuildTreeItem(thread.Thread, root));
            }

            ExpandToFirstBranching(threadItem);
            ThreadItems.Add(threadItem);
        }

        OnPropertyChanged(nameof(HasThreadItems));
    }

    private static ThreadTreeItemViewModel BuildTreeItem(ThreadInfo thread, CallTreeNode node)
    {
        var hiddenChainNames = new List<string>();
        IReadOnlyList<CallTreeNode> visibleChildren = node.Children;
        var currentNode = node;

        while (currentNode.Children.Count == 1)
        {
            var nextNode = currentNode.Children[0];
            if (nextNode.Children.Count > 1)
            {
                visibleChildren = [nextNode];
                break;
            }

            hiddenChainNames.Add(nextNode.Name);
            currentNode = nextNode;
            visibleChildren = nextNode.Children;

            if (nextNode.Children.Count == 0)
            {
                visibleChildren = Array.Empty<CallTreeNode>();
                break;
            }
        }

        var item = new ThreadTreeItemViewModel
        {
            Thread = thread,
            Node = node,
            DisplayName = node.Name,
            DurationText = $"{node.InclusiveMilliseconds:F3} ms",
            HiddenChainNames = hiddenChainNames,
        };

        foreach (var child in visibleChildren)
        {
            item.Children.Add(BuildTreeItem(thread, child));
        }

        return item;
    }

    private void ClearTraceView()
    {
        FrameBars.Clear();
        ClearSelectedTreeItems();
        _selectionAnchor = null;
        ThreadItems.Clear();
        HasSelectedNode = false;
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(HasFrameBars));
        OnPropertyChanged(nameof(HasThreadItems));
        ResetDetails();
    }

    private void ResetDetails()
    {
        ReplaceDetails(
        [
            CreateDetail("Name", "-"),
            CreateDetail("Thread", "-"),
            CreateDetail("Inclusive ms", "-"),
            CreateDetail("Exclusive ms", "-"),
            CreateDetail("Children ms", "-"),
            CreateDetail("Start", "-"),
            CreateDetail("End", "-"),
            CreateDetail("StartedBeforeFrame", "-"),
            CreateDetail("EndedAfterFrame", "-"),
        ]);
    }

    private void ReplaceDetails(IReadOnlyList<FunctionDetailItemViewModel> items)
    {
        Details.Clear();
        foreach (var item in items)
        {
            Details.Add(item);
        }
    }

    private static FunctionDetailItemViewModel CreateDetail(string label, string value)
    {
        return new FunctionDetailItemViewModel { Label = label, Value = value };
    }

    private static IEnumerable<int> EnumerateCandidateIndices(int frameCount)
    {
        var indices = new SortedSet<int>();
        for (var index = 0; index < Math.Min(frameCount, 120); index += 4)
        {
            indices.Add(index);
        }

        if (frameCount > 0)
        {
            indices.Add(frameCount / 4);
            indices.Add(frameCount / 2);
            indices.Add(Math.Max(0, frameCount - 1));
        }

        return indices;
    }

    private static string? FindDefaultTracePath()
    {
        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var candidate = Directory.GetFiles(current, "*.utrace").FirstOrDefault();
            if (candidate is not null)
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        return null;
    }

    private void ClearSelectedTreeItems()
    {
        foreach (var item in _selectedThreadItems)
        {
            item.IsSelected = false;
        }

        _selectedThreadItems.Clear();
    }

    private IEnumerable<ThreadTreeItemViewModel> EnumerateSelectableTreeItems()
    {
        foreach (var threadItem in ThreadItems)
        {
            foreach (var child in EnumerateSelectableTreeItems(threadItem))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<ThreadTreeItemViewModel> EnumerateSelectableTreeItems(ThreadTreeItemViewModel item)
    {
        if (item.Node is not null)
        {
            yield return item;
        }

        if (item.Node is not null && !item.IsExpanded)
        {
            yield break;
        }

        foreach (var child in item.Children)
        {
            foreach (var nestedChild in EnumerateSelectableTreeItems(child))
            {
                yield return nestedChild;
            }
        }
    }
}
