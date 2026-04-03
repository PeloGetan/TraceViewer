using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using TraceViewer.Diagnostics;
using TraceViewer.Import;
using TraceViewer.Query;
using TraceViewer.SessionModel;

namespace TraceViewer.UI.ViewModels;

public sealed class ProfilerScreenViewModel : ViewModelBase
{
    private const double FrameBarSpacing = 2.0;
    private const double GuideLabelMinimumOffsetFromBottom = 10.0;
    private const double GuideLabelMaximumPadding = 8.0;
    private const double GuideLabelMinimumGap = 18.0;
    private const double ThirtyFramesPerSecondBudgetSeconds = 1.0 / 30.0;
    private const double SixtyFramesPerSecondBudgetSeconds = 1.0 / 60.0;
    private const double OneHundredTwentyFramesPerSecondBudgetSeconds = 1.0 / 120.0;
    public const double MinFrameBarWidth = 4.0;
    public const double MaxFrameBarWidth = 36.0;
    public const double DefaultFrameBarWidth = 8.0;
    public const double MinFrameBarMaxHeight = 40.0;
    public const double MaxFrameBarMaxHeight = 600.0;
    public const double DefaultFrameBarMaxHeight = 150.0;
    public const double MinRenderedFrameBarHeight = 4.0;

    private readonly TraceImportService _traceImportService;
    private readonly FrameTimelineQuery _frameTimelineQuery;
    private readonly ThreadActivityQuery _threadActivityQuery;
    private readonly FunctionDetailsQuery _detailsQuery;
    private readonly List<ThreadTreeItemViewModel> _selectedThreadItems = [];
    private readonly string _applicationVersion;
    private ThreadTreeItemViewModel? _selectionAnchor;
    private TraceImportResult? _importResult;
    private string _statusText;
    private double _frameBarWidth = DefaultFrameBarWidth;
    private double _frameBarMaxHeight = DefaultFrameBarMaxHeight;
    private double _timelineHorizontalOffset;
    private double _timelineViewportWidth;
    private bool _animateFrameBarHeights;
    private bool _suspendFrameBarHeightRefresh;
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
        _applicationVersion = ResolveApplicationVersion();
        _statusText = BuildStatusMessage("Open a local .utrace to populate frames, active threads, and function details.");
        FrameBudgetGuides =
        [
            new FrameBudgetGuideViewModel
            {
                DurationSeconds = ThirtyFramesPerSecondBudgetSeconds,
                Label = "33.33 ms",
                Brush = "#E05555",
            },
            new FrameBudgetGuideViewModel
            {
                DurationSeconds = SixtyFramesPerSecondBudgetSeconds,
                Label = "16.67 ms",
                Brush = "#59C36A",
            },
            new FrameBudgetGuideViewModel
            {
                DurationSeconds = OneHundredTwentyFramesPerSecondBudgetSeconds,
                Label = "8.33 ms",
                Brush = "#4A90E2",
            },
            new FrameBudgetGuideViewModel
            {
                DurationSeconds = SixtyFramesPerSecondBudgetSeconds,
                Label = "M",
                Brush = "#8B5CF6",
            },
        ];
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

    public IReadOnlyList<FrameBudgetGuideViewModel> FrameBudgetGuides { get; }

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
            RefreshFrameBarHeights();
        }
    }

    public bool HasFrameBars => FrameBars.Count > 0;

    public bool HasThreadItems => ThreadItems.Count > 0;

    public bool HasSelectedNode { get; private set; }

    public bool HasFrameBudgetGuide => HasFrameBars;

    public double FrameBudgetGuideTranslateY => SixtyFrameBudgetGuideTranslateY;

    public double ThirtyFrameBudgetGuideTranslateY => FrameBudgetGuides[0].TranslateY;

    public double SixtyFrameBudgetGuideTranslateY => FrameBudgetGuides[1].TranslateY;

    public double OneTwentyFrameBudgetGuideTranslateY => FrameBudgetGuides[2].TranslateY;

    public double MedianFrameBudgetGuideTranslateY => FrameBudgetGuides[3].TranslateY;

    public bool AnimateFrameBarHeights
    {
        get => _animateFrameBarHeights;
        private set
        {
            if (_animateFrameBarHeights == value)
            {
                return;
            }

            _animateFrameBarHeights = value;
            OnPropertyChanged();
        }
    }

    public double FrameBarMaxHeight
    {
        get => _frameBarMaxHeight;
        set
        {
            var clamped = Math.Clamp(value, MinFrameBarMaxHeight, MaxFrameBarMaxHeight);
            if (Math.Abs(_frameBarMaxHeight - clamped) < 0.001)
            {
                return;
            }

            _frameBarMaxHeight = clamped;
            OnPropertyChanged();
            RefreshFrameBarHeights();
        }
    }

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

    public void SetFrameBarHeightAnimation(bool enabled)
    {
        AnimateFrameBarHeights = enabled;
    }

    public void BeginFrameBarInteraction()
    {
        _suspendFrameBarHeightRefresh = true;
        AnimateFrameBarHeights = false;
    }

    public void EndFrameBarInteraction(bool animate)
    {
        _suspendFrameBarHeightRefresh = false;
        AnimateFrameBarHeights = animate;
        RefreshFrameBarHeights();
    }

    public void AdjustFrameBarWidth(double delta)
    {
        AnimateFrameBarHeights = false;
        FrameBarWidth += delta;
    }

    public void AdjustFrameBarHeightScale(double delta)
    {
        AnimateFrameBarHeights = false;
        FrameBarMaxHeight += delta;
    }

    public void UpdateFrameBarViewport(double horizontalOffset, double viewportWidth, bool animate = false)
    {
        AnimateFrameBarHeights = animate;
        _timelineHorizontalOffset = Math.Max(0.0, horizontalOffset);
        _timelineViewportWidth = Math.Max(0.0, viewportWidth);
        RefreshFrameBarHeights();
    }

    public async Task<bool> TryLoadDefaultTraceAsync()
    {
        var tracePath = FindDefaultTracePath();
        if (tracePath is null)
        {
            StatusText = BuildStatusMessage("No local .utrace found yet. Use Open Trace to load a file.");
            return false;
        }

        await LoadTraceAsync(tracePath);
        return true;
    }

    public async Task LoadTraceAsync(string traceFilePath)
    {
        RuntimeLoadLogger.EnsureSession(traceFilePath, nameof(LoadTraceAsync));
        RuntimeLoadLogger.Log("load-start", $"path={traceFilePath}");
        IsLoading = true;
        StatusText = BuildStatusMessage($"Loading {Path.GetFileName(traceFilePath)}...");

        try
        {
            var importWatch = Stopwatch.StartNew();
            var result = await Task.Run(() => _traceImportService.Import(traceFilePath));
            importWatch.Stop();
            RuntimeLoadLogger.Log(
                "import-finished",
                $"durationMs={importWatch.Elapsed.TotalMilliseconds:F3};eventCount={result.ReadResult.EventCount};packetCount={result.ReadResult.PacketCount}");

            var applyWatch = Stopwatch.StartNew();
            ApplyImportResult(result, traceFilePath);
            applyWatch.Stop();
            RuntimeLoadLogger.Log(
                "apply-finished",
                $"durationMs={applyWatch.Elapsed.TotalMilliseconds:F3};frameBars={FrameBars.Count};threadItems={ThreadItems.Count}");
        }
        catch (Exception exception)
        {
            RuntimeLoadLogger.Log("load-failed", $"{exception.GetType().Name}: {exception.Message}");
            StatusText = BuildStatusMessage($"Failed to load {Path.GetFileName(traceFilePath)}: {exception.Message}");
            ClearTraceView();
        }
        finally
        {
            IsLoading = false;
            RuntimeLoadLogger.Log("loading-finished", $"isLoading={IsLoading}");
        }
    }

    public void ApplyImportResult(TraceImportResult result, string sourceName)
    {
        _importResult = result;
        AnimateFrameBarHeights = false;
        RebuildFrameBars(result.Session);

        var completedFrames = result.Session.Frames
            .GetSeries(FrameType.Game)
            .Frames
            .Where(frame => double.IsFinite(frame.EndTime))
            .ToArray();

        if (completedFrames.Length == 0)
        {
            StatusText = BuildStatusMessage($"Loaded {Path.GetFileName(sourceName)}, but no completed Game frames were found.");
            RefreshThreadItems(Array.Empty<ThreadFrameView>());
            ResetDetails();
            return;
        }

        var selectedFrame = FindDefaultFrame(result.Session, completedFrames) ?? completedFrames[0];
        SelectFrame(selectedFrame);

        var threadCount = result.Session.Threads.GetOrderedThreads().Count;
        StatusText = BuildStatusMessage($"Loaded {Path.GetFileName(sourceName)}: {completedFrames.Length} Game frames, {threadCount} threads, {result.Session.CpuTimelines.Timelines.Count} CPU timelines.");
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
        var selectedFrame = FrameBars.FirstOrDefault(bar => bar.IsSelected)?.Frame;
        FrameBars.Clear();

        var gameSeries = session.Frames.GetSeries(FrameType.Game);
        var completedFrames = gameSeries.Frames.Where(frame => double.IsFinite(frame.EndTime)).ToArray();
        if (completedFrames.Length == 0)
        {
            OnPropertyChanged(nameof(HasFrameBars));
            OnPropertyChanged(nameof(HasFrameBudgetGuide));
            return;
        }

        var startTime = completedFrames[0].StartTime;
        var endTime = completedFrames[^1].EndTime;
        foreach (var bar in _frameTimelineQuery.GetBars(gameSeries, startTime, endTime))
        {
            FrameBars.Add(new FrameBarViewModel
            {
                Frame = bar.Frame,
                DurationSeconds = bar.DurationSeconds,
                Height = MinRenderedFrameBarHeight,
                Tooltip = $"Frame {bar.Frame.Index}: {((bar.Frame.EndTime - bar.Frame.StartTime) * 1000.0).ToString("F3", CultureInfo.InvariantCulture)} ms",
                IsSelected = ReferenceEquals(bar.Frame, selectedFrame),
            });
        }

        OnPropertyChanged(nameof(HasFrameBars));
        OnPropertyChanged(nameof(HasFrameBudgetGuide));
        RefreshFrameBarHeights();
    }

    private void RefreshFrameBarHeights()
    {
        if (_suspendFrameBarHeightRefresh)
        {
            return;
        }

        if (FrameBars.Count == 0)
        {
            return;
        }

        var visibleBars = GetVisibleFrameBars();
        if (visibleBars.Count == 0)
        {
            return;
        }

        var minDuration = visibleBars.Min(bar => bar.DurationSeconds);
        var maxDuration = visibleBars.Max(bar => bar.DurationSeconds);

        foreach (var barViewModel in FrameBars)
        {
            barViewModel.Height = CalculateFrameBarHeight(barViewModel.DurationSeconds, minDuration, maxDuration);
        }

        UpdateFrameBudgetGuides(minDuration, maxDuration);
    }

    private IReadOnlyList<FrameBarViewModel> GetVisibleFrameBars()
    {
        if (_timelineViewportWidth <= 0.0)
        {
            return FrameBars;
        }

        var slotWidth = FrameBarWidth + FrameBarSpacing;
        if (slotWidth <= 0.0)
        {
            return FrameBars;
        }

        var startIndex = Math.Clamp((int)Math.Floor(_timelineHorizontalOffset / slotWidth), 0, Math.Max(0, FrameBars.Count - 1));
        var visibleCount = Math.Max(1, (int)Math.Ceiling(_timelineViewportWidth / slotWidth) + 1);
        var count = Math.Min(visibleCount, FrameBars.Count - startIndex);
        return FrameBars.Skip(startIndex).Take(count).ToArray();
    }

    private double CalculateFrameBarHeight(double durationSeconds, double minDuration, double maxDuration)
    {
        if (maxDuration - minDuration <= double.Epsilon)
        {
            return FrameBarMaxHeight;
        }

        var normalized = Math.Clamp((durationSeconds - minDuration) / (maxDuration - minDuration), 0.0, 1.0);
        return MinRenderedFrameBarHeight + (normalized * (FrameBarMaxHeight - MinRenderedFrameBarHeight));
    }

    private void UpdateFrameBudgetGuides(double minDuration, double maxDuration)
    {
        var medianGuide = FrameBudgetGuides[3];
        var medianDuration = CalculateVisibleMedianDuration();
        medianGuide.DurationSeconds = medianDuration;
        medianGuide.Label = $"{(medianDuration * 1000.0).ToString("F2", CultureInfo.InvariantCulture)} ms";

        var labelOffsets = new List<(FrameBudgetGuideViewModel Guide, double OffsetFromBottom)>(FrameBudgetGuides.Count);
        foreach (var guide in FrameBudgetGuides)
        {
            var offsetFromBottom = CalculateFrameBarHeight(guide.DurationSeconds, minDuration, maxDuration);
            guide.TranslateY = -offsetFromBottom;
            labelOffsets.Add((guide, offsetFromBottom));
        }

        ApplyGuideLabelLayout(labelOffsets);

        OnPropertyChanged(nameof(ThirtyFrameBudgetGuideTranslateY));
        OnPropertyChanged(nameof(FrameBudgetGuideTranslateY));
        OnPropertyChanged(nameof(SixtyFrameBudgetGuideTranslateY));
        OnPropertyChanged(nameof(OneTwentyFrameBudgetGuideTranslateY));
        OnPropertyChanged(nameof(MedianFrameBudgetGuideTranslateY));
    }

    private double CalculateVisibleMedianDuration()
    {
        var visibleDurations = GetVisibleFrameBars()
            .Select(bar => bar.DurationSeconds)
            .OrderBy(duration => duration)
            .ToArray();

        if (visibleDurations.Length == 0)
        {
            return SixtyFramesPerSecondBudgetSeconds;
        }

        var middleIndex = visibleDurations.Length / 2;
        if ((visibleDurations.Length & 1) != 0)
        {
            return visibleDurations[middleIndex];
        }

        return (visibleDurations[middleIndex - 1] + visibleDurations[middleIndex]) * 0.5;
    }

    private void ApplyGuideLabelLayout(List<(FrameBudgetGuideViewModel Guide, double OffsetFromBottom)> labelOffsets)
    {
        if (labelOffsets.Count == 0)
        {
            return;
        }

        labelOffsets.Sort((left, right) => left.OffsetFromBottom.CompareTo(right.OffsetFromBottom));
        var assignedOffsets = new double[labelOffsets.Count];
        var previousOffset = GuideLabelMinimumOffsetFromBottom - GuideLabelMinimumGap;

        for (var index = 0; index < labelOffsets.Count; index++)
        {
            assignedOffsets[index] = Math.Max(labelOffsets[index].OffsetFromBottom, previousOffset + GuideLabelMinimumGap);
            previousOffset = assignedOffsets[index];
        }

        var maximumOffset = Math.Max(
            GuideLabelMinimumOffsetFromBottom,
            FrameBarMaxHeight - GuideLabelMaximumPadding);

        if (assignedOffsets[^1] > maximumOffset)
        {
            assignedOffsets[^1] = maximumOffset;
            for (var index = assignedOffsets.Length - 2; index >= 0; index--)
            {
                assignedOffsets[index] = Math.Min(
                    assignedOffsets[index],
                    assignedOffsets[index + 1] - GuideLabelMinimumGap);
            }

            if (assignedOffsets[0] < GuideLabelMinimumOffsetFromBottom)
            {
                var shift = GuideLabelMinimumOffsetFromBottom - assignedOffsets[0];
                for (var index = 0; index < assignedOffsets.Length; index++)
                {
                    assignedOffsets[index] += shift;
                }
            }
        }

        for (var index = 0; index < labelOffsets.Count; index++)
        {
            labelOffsets[index].Guide.LabelTranslateY = -assignedOffsets[index];
        }
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
        OnPropertyChanged(nameof(HasFrameBudgetGuide));
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

    private string BuildStatusMessage(string message)
    {
        return $"TraceViewer {_applicationVersion}  |  {message}";
    }

    private static string ResolveApplicationVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            return plusIndex > 0 ? informationalVersion[..plusIndex] : informationalVersion;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
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
