using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using TraceViewer.Diagnostics;
using TraceViewer.UI.ViewModels;

namespace TraceViewer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const string AutoLoadTraceEnvVar = "UEPROFILEREADER_AUTOLOAD_TRACE";
    private const string BenchmarkTraceEnvVar = "TRACEVIEWER_BENCHMARK_TRACE";
    private const string BenchmarkOutputEnvVar = "TRACEVIEWER_BENCHMARK_OUTPUT";
    private static readonly TimeSpan FrameBarAnimationIdleDelay = TimeSpan.FromMilliseconds(120);

    private bool _isTimelinePanning;
    private Point _lastTimelinePanPoint;
    private readonly DispatcherTimer _frameTimelineIdleTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ProfilerScreenViewModel();
        Loaded += MainWindow_Loaded;
        _frameTimelineIdleTimer = new DispatcherTimer
        {
            Interval = FrameBarAnimationIdleDelay,
        };
        _frameTimelineIdleTimer.Tick += FrameTimelineIdleTimer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilerScreenViewModel viewModel)
        {
            return;
        }

        var benchmarkTracePath = Environment.GetEnvironmentVariable(BenchmarkTraceEnvVar);
        if (!string.IsNullOrWhiteSpace(benchmarkTracePath))
        {
            await RunBenchmarkLoadAsync(viewModel, benchmarkTracePath);
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(AutoLoadTraceEnvVar), "1", StringComparison.Ordinal))
        {
            await viewModel.TryLoadDefaultTraceAsync();
        }

        await SyncFrameTimelineViewportAsync(animate: false);
    }

    private async void OpenTraceButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilerScreenViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Unreal Trace (*.utrace)|*.utrace|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            RuntimeLoadLogger.BeginSession(dialog.FileName, nameof(OpenTraceButton_Click));
            RuntimeLoadLogger.Log("file-selected", $"path={dialog.FileName}");
            await viewModel.LoadTraceAsync(dialog.FileName);
            RuntimeLoadLogger.Log("sync-viewport-start");
            await SyncFrameTimelineViewportAsync(animate: false);
            RuntimeLoadLogger.Log("sync-viewport-finished");
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            RuntimeLoadLogger.Log("first-render-finished");
        }
    }

    private void FrameBar_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilerScreenViewModel viewModel ||
            sender is not FrameworkElement { Tag: FrameBarViewModel frameBar })
        {
            return;
        }

        viewModel.SelectFrame(frameBar);
    }

    private void ThreadTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ProfilerScreenViewModel viewModel)
        {
            viewModel.SelectTreeItem(e.NewValue as ThreadTreeItemViewModel);
        }
    }

    private void ThreadTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ProfilerScreenViewModel viewModel)
        {
            return;
        }

        var treeViewItem = FindAncestorOrSelf<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (treeViewItem?.DataContext is not ThreadTreeItemViewModel item)
        {
            return;
        }

        var appendSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        viewModel.ToggleTreeItemSelection(item, appendSelection);
    }

    private void ThreadTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ProfilerScreenViewModel viewModel)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            var text = viewModel.BuildSelectedNamesClipboardText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Clipboard.SetText(text);
            }

            e.Handled = true;
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        if (ThreadTreeView.SelectedItem is not ThreadTreeItemViewModel item)
        {
            return;
        }

        if (e.Key == Key.Right)
        {
            viewModel.SetExpandedRecursively(item, true);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            viewModel.SetExpandedRecursively(item, false);
            e.Handled = true;
        }
    }

    private void ThreadTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilerScreenViewModel viewModel ||
            e.OriginalSource is not TreeViewItem treeViewItem ||
            treeViewItem.DataContext is not ThreadTreeItemViewModel item)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            viewModel.SetExpandedRecursively(item, true);
            return;
        }

        viewModel.ExpandToFirstBranching(item);
    }

    private void ThreadTreeItem_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
            DataContext is not ProfilerScreenViewModel viewModel ||
            e.OriginalSource is not TreeViewItem treeViewItem ||
            treeViewItem.DataContext is not ThreadTreeItemViewModel item)
        {
            return;
        }

        viewModel.SetExpandedRecursively(item, false);
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilerScreenViewModel viewModel)
        {
            viewModel.AdjustFrameBarWidth(-2.0);
        }
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilerScreenViewModel viewModel)
        {
            viewModel.AdjustFrameBarWidth(2.0);
        }
    }

    private void FrameTimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || DataContext is not ProfilerScreenViewModel viewModel)
        {
            return;
        }

        BeginFrameTimelineInteraction(viewModel);

        if (Keyboard.IsKeyDown(Key.LeftCtrl))
        {
            viewModel.AdjustFrameBarHeightScale(e.Delta > 0 ? 20.0 : -20.0);
            viewModel.UpdateFrameBarViewport(scrollViewer.HorizontalOffset, scrollViewer.ViewportWidth, animate: false);
            e.Handled = true;
            return;
        }

        var oldWidth = viewModel.FrameBarWidth;
        var oldExtent = scrollViewer.ExtentWidth;
        var position = e.GetPosition(scrollViewer);
        var anchorOffset = scrollViewer.HorizontalOffset + position.X;

        viewModel.AdjustFrameBarWidth(e.Delta > 0 ? 2.0 : -2.0);
        if (Math.Abs(oldWidth - viewModel.FrameBarWidth) < 0.001)
        {
            return;
        }

        scrollViewer.UpdateLayout();
        var newExtent = scrollViewer.ExtentWidth;
        if (oldExtent > 0 && newExtent > 0)
        {
            var ratio = anchorOffset / oldExtent;
            var targetOffset = (ratio * newExtent) - position.X;
            scrollViewer.ScrollToHorizontalOffset(Math.Max(0.0, Math.Min(targetOffset, scrollViewer.ScrollableWidth)));
        }

        viewModel.UpdateFrameBarViewport(scrollViewer.HorizontalOffset, scrollViewer.ViewportWidth, animate: false);

        e.Handled = true;
    }

    private void FrameTimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer && DataContext is ProfilerScreenViewModel viewModel)
        {
            BeginFrameTimelineInteraction(viewModel);
            viewModel.UpdateFrameBarViewport(scrollViewer.HorizontalOffset, scrollViewer.ViewportWidth, animate: false);
        }
    }

    private void FrameTimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer && DataContext is ProfilerScreenViewModel viewModel)
        {
            BeginFrameTimelineInteraction(viewModel);
            viewModel.UpdateFrameBarViewport(scrollViewer.HorizontalOffset, scrollViewer.ViewportWidth, animate: false);
        }
    }

    private void FrameTimelineScrollViewer_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _isTimelinePanning = true;
        _lastTimelinePanPoint = e.GetPosition(scrollViewer);
        if (DataContext is ProfilerScreenViewModel viewModel)
        {
            BeginFrameTimelineInteraction(viewModel);
        }
        scrollViewer.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeWE;
        e.Handled = true;
    }

    private void FrameTimelineScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTimelinePanning || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var currentPoint = e.GetPosition(scrollViewer);
        var delta = currentPoint.X - _lastTimelinePanPoint.X;
        _lastTimelinePanPoint = currentPoint;
        if (DataContext is ProfilerScreenViewModel viewModel)
        {
            BeginFrameTimelineInteraction(viewModel);
        }
        scrollViewer.ScrollToHorizontalOffset(Math.Max(0.0, Math.Min(scrollViewer.HorizontalOffset - delta, scrollViewer.ScrollableWidth)));
        e.Handled = true;
    }

    private void FrameTimelineScrollViewer_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndTimelinePan(sender as ScrollViewer);
        e.Handled = true;
    }

    private void FrameTimelineScrollViewer_LostMouseCapture(object sender, MouseEventArgs e)
    {
        EndTimelinePan(sender as ScrollViewer);
    }

    private void Window_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndTimelinePan(FrameTimelineScrollViewer);
    }

    private void EndTimelinePan(ScrollViewer? scrollViewer)
    {
        _isTimelinePanning = false;
        scrollViewer?.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
    }

    private void BeginFrameTimelineInteraction(ProfilerScreenViewModel viewModel)
    {
        viewModel.BeginFrameBarInteraction();
        _frameTimelineIdleTimer.Stop();
        _frameTimelineIdleTimer.Start();
    }

    private void FrameTimelineIdleTimer_Tick(object? sender, EventArgs e)
    {
        _frameTimelineIdleTimer.Stop();
        if (DataContext is ProfilerScreenViewModel viewModel)
        {
            viewModel.EndFrameBarInteraction(animate: true);
        }
    }

    private async Task SyncFrameTimelineViewportAsync(bool animate)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is not ProfilerScreenViewModel viewModel)
            {
                return;
            }

            FrameTimelineScrollViewer.UpdateLayout();
            viewModel.BeginFrameBarInteraction();
            viewModel.UpdateFrameBarViewport(
                FrameTimelineScrollViewer.HorizontalOffset,
                FrameTimelineScrollViewer.ViewportWidth,
                animate: false);
            viewModel.EndFrameBarInteraction(animate);
        }, DispatcherPriority.Loaded);
    }

    private async Task RunBenchmarkLoadAsync(ProfilerScreenViewModel viewModel, string traceFilePath)
    {
        ShowInTaskbar = false;
        Left = -20000;
        Top = -20000;

        var watch = Stopwatch.StartNew();

        try
        {
            await viewModel.LoadTraceAsync(traceFilePath);
            var afterLoad = watch.Elapsed;

            await SyncFrameTimelineViewportAsync(animate: false);
            var afterSync = watch.Elapsed;

            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var afterRender = watch.Elapsed;

            WriteBenchmarkReport(traceFilePath, afterLoad, afterSync, afterRender, viewModel);
        }
        finally
        {
            Close();
        }
    }

    private void WriteBenchmarkReport(
        string traceFilePath,
        TimeSpan afterLoad,
        TimeSpan afterSync,
        TimeSpan afterRender,
        ProfilerScreenViewModel viewModel)
    {
        var outputPath = Environment.GetEnvironmentVariable(BenchmarkOutputEnvVar);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var lines = new[]
        {
            $"trace={traceFilePath}",
            $"load={afterLoad.TotalSeconds:F3}",
            $"sync={afterSync.TotalSeconds:F3}",
            $"render={afterRender.TotalSeconds:F3}",
            $"frameBars={viewModel.FrameBars.Count}",
            $"threadItems={viewModel.ThreadItems.Count}",
        };

        File.WriteAllLines(outputPath, lines);
    }

    private void RepositoryLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true,
        });

        e.Handled = true;
    }

    private static T? FindAncestorOrSelf<T>(DependencyObject? dependencyObject)
        where T : DependencyObject
    {
        while (dependencyObject is not null)
        {
            if (dependencyObject is T match)
            {
                return match;
            }

            dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
        }

        return null;
    }
}
