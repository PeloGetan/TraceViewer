using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TraceViewer;
using TraceViewer.Import;
using TraceViewer.UI.ViewModels;

namespace TraceViewer.Benchmarks;

internal static class Program
{
    private static volatile string _currentStage = "init";
    private static Timer? _watchdog;

    private static int Main(string[] args)
    {
        var tracePath = args.Length > 0
            ? args[0]
            : Path.Combine(Environment.CurrentDirectory, "test_01.utrace");

        if (!File.Exists(tracePath))
        {
            Console.Error.WriteLine($"Trace file was not found: {tracePath}");
            return 1;
        }

        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                Run(tracePath);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static void Run(string tracePath)
    {
        using var watchdog = new Timer(OnWatchdogElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watchdog = watchdog;
        Environment.SetEnvironmentVariable("UEPROFILEREADER_AUTOLOAD_TRACE", null);
        _ = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        Console.WriteLine($"trace: {tracePath}");
        Console.WriteLine($"started-at: {DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)}");

        var importService = new TraceImportService();

        TouchStage("import");
        var importWatch = Stopwatch.StartNew();
        var importResult = importService.Import(tracePath);
        importWatch.Stop();
        Console.WriteLine($"import-only: {importWatch.Elapsed.TotalSeconds:F3}s");

        TouchStage("detached-viewmodel");
        var detachedViewModel = new ProfilerScreenViewModel();
        var detachedWatch = Stopwatch.StartNew();
        detachedViewModel.ApplyImportResult(importResult, tracePath);
        detachedWatch.Stop();
        PrintViewModelSummary("detached", detachedViewModel, detachedWatch.Elapsed);

        TouchStage("timeline-window");
        var timelineWindow = new BenchmarkWindow(new ProfilerScreenViewModel(), BenchmarkSurface.TimelineOnly);
        timelineWindow.Show();
        timelineWindow.UpdateLayout();
        MeasureBoundScenario("timeline", timelineWindow, importResult, tracePath);

        TouchStage("tree-window");
        var treeWindow = new BenchmarkWindow(new ProfilerScreenViewModel(), BenchmarkSurface.TreeOnly);
        treeWindow.Show();
        treeWindow.UpdateLayout();
        MeasureBoundScenario("tree", treeWindow, importResult, tracePath);

        TouchStage("full-window");
        var fullWindow = new BenchmarkWindow(new ProfilerScreenViewModel(), BenchmarkSurface.Full);
        fullWindow.Show();
        fullWindow.UpdateLayout();
        MeasureBoundScenario("full", fullWindow, importResult, tracePath);

        MeasureRealMainWindowScenario(tracePath);
        MeasureExternalProcessScenario(tracePath);

        TouchStage("done");
        _watchdog?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private static void MeasureRealMainWindowScenario(string tracePath)
    {
        TouchStage("real-window-create");
        var window = new MainWindow
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };

        TouchStage("real-window-show");
        window.Show();
        window.UpdateLayout();

        if (window.DataContext is not ProfilerScreenViewModel viewModel)
        {
            throw new InvalidOperationException("MainWindow DataContext is not a ProfilerScreenViewModel.");
        }

        var importService = new TraceImportService();
        var watch = Stopwatch.StartNew();

        TouchStage("real-window-import");
        var importResult = importService.Import(tracePath);
        var afterImport = watch.Elapsed;

        TouchStage("real-window-apply");
        viewModel.ApplyImportResult(importResult, tracePath);
        var afterApply = watch.Elapsed;

        TouchStage("real-window-sync-viewport");
        SyncViewport(window, viewModel);
        var afterSync = watch.Elapsed;

        TouchStage("real-window-layout");
        window.UpdateLayout();
        var afterLayout = watch.Elapsed;

        TouchStage("real-window-render");
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var afterRender = watch.Elapsed;

        Console.WriteLine($"real-window-import: {afterImport.TotalSeconds:F3}s");
        Console.WriteLine($"real-window-apply: {afterApply.TotalSeconds:F3}s");
        Console.WriteLine($"real-window-sync-viewport: {afterSync.TotalSeconds:F3}s");
        Console.WriteLine($"real-window-layout: {afterLayout.TotalSeconds:F3}s");
        Console.WriteLine($"real-window-render: {afterRender.TotalSeconds:F3}s");
        Console.WriteLine($"real-window-frame-bars: {viewModel.FrameBars.Count}");
        Console.WriteLine($"real-window-top-level-thread-items: {viewModel.ThreadItems.Count}");
        Console.WriteLine($"real-window-total-tree-nodes: {CountTreeNodes(viewModel.ThreadItems):N0}");

        window.Close();
    }

    private static void MeasureExternalProcessScenario(string tracePath)
    {
        var executablePath = Path.Combine(
            Environment.CurrentDirectory,
            "TraceViewer",
            "bin",
            "Release",
            "net8.0-windows",
            "TraceViewer.exe");

        if (!File.Exists(executablePath))
        {
            Console.WriteLine($"external-process-skipped: missing {executablePath}");
            return;
        }

        var reportPath = Path.Combine(Path.GetTempPath(), $"traceviewer-benchmark-{Guid.NewGuid():N}.txt");
        try
        {
            TouchStage("external-process");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                },
            };

            process.StartInfo.EnvironmentVariables["TRACEVIEWER_BENCHMARK_TRACE"] = Path.GetFullPath(tracePath);
            process.StartInfo.EnvironmentVariables["TRACEVIEWER_BENCHMARK_OUTPUT"] = reportPath;

            var watch = Stopwatch.StartNew();
            process.Start();
            process.WaitForExit(180_000);
            watch.Stop();

            Console.WriteLine($"external-process-wall-clock: {watch.Elapsed.TotalSeconds:F3}s");
            Console.WriteLine($"external-process-exit-code: {process.ExitCode}");

            if (!File.Exists(reportPath))
            {
                Console.WriteLine("external-process-report: missing");
                return;
            }

            foreach (var line in File.ReadAllLines(reportPath).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                Console.WriteLine($"external-{line}");
            }
        }
        finally
        {
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }

    private static void MeasureBoundScenario(
        string label,
        BenchmarkWindow window,
        TraceImportResult importResult,
        string tracePath)
    {
        var viewModel = window.ViewModel;
        var watch = Stopwatch.StartNew();

        TouchStage($"{label}-apply");
        viewModel.ApplyImportResult(importResult, tracePath);
        var afterApply = watch.Elapsed;

        TouchStage($"{label}-layout");
        window.UpdateLayout();
        var afterLayout = watch.Elapsed;

        TouchStage($"{label}-render");
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var afterRender = watch.Elapsed;

        Console.WriteLine($"{label}-bound-apply: {afterApply.TotalSeconds:F3}s");
        Console.WriteLine($"{label}-bound-layout: {afterLayout.TotalSeconds:F3}s");
        Console.WriteLine($"{label}-bound-render: {afterRender.TotalSeconds:F3}s");
        Console.WriteLine($"{label}-frame-bars: {viewModel.FrameBars.Count}");
        Console.WriteLine($"{label}-top-level-thread-items: {viewModel.ThreadItems.Count}");
        Console.WriteLine($"{label}-total-tree-nodes: {CountTreeNodes(viewModel.ThreadItems):N0}");

        window.Close();
    }

    private static void PrintViewModelSummary(string label, ProfilerScreenViewModel viewModel, TimeSpan elapsed)
    {
        Console.WriteLine($"{label}-apply: {elapsed.TotalSeconds:F3}s");
        Console.WriteLine($"{label}-frame-bars: {viewModel.FrameBars.Count}");
        Console.WriteLine($"{label}-top-level-thread-items: {viewModel.ThreadItems.Count}");
        Console.WriteLine($"{label}-total-tree-nodes: {CountTreeNodes(viewModel.ThreadItems):N0}");
    }

    private static int CountTreeNodes(IEnumerable<ThreadTreeItemViewModel> items)
    {
        var count = 0;
        foreach (var item in items)
        {
            count += CountTreeNodes(item);
        }

        return count;
    }

    private static int CountTreeNodes(ThreadTreeItemViewModel item)
    {
        var count = 1;
        foreach (var child in item.Children)
        {
            count += CountTreeNodes(child);
        }

        return count;
    }

    private static void TouchStage(string stage)
    {
        _currentStage = stage;
        _watchdog?.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }

    private static void OnWatchdogElapsed(object? state)
    {
        Console.Error.WriteLine($"TIMEOUT during stage: {_currentStage}");
        Environment.Exit(124);
    }

    private static void SyncViewport(MainWindow window, ProfilerScreenViewModel viewModel)
    {
        var scrollViewer = window.FindName("FrameTimelineScrollViewer") as ScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        viewModel.BeginFrameBarInteraction();
        viewModel.UpdateFrameBarViewport(scrollViewer.HorizontalOffset, scrollViewer.ViewportWidth, animate: false);
        viewModel.EndFrameBarInteraction(animate: false);
    }

    private sealed class BenchmarkWindow : Window
    {
        public BenchmarkWindow(ProfilerScreenViewModel viewModel, BenchmarkSurface surface)
        {
            Width = 1440;
            Height = surface == BenchmarkSurface.TimelineOnly ? 260 : 840;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = -20000;
            DataContext = viewModel;
            ViewModel = viewModel;
            Content = surface switch
            {
                BenchmarkSurface.TimelineOnly => BuildTimelineHost(),
                BenchmarkSurface.TreeOnly => BuildTreeHost(),
                _ => BuildFullHost(),
            };
        }

        public ProfilerScreenViewModel ViewModel { get; }

        private UIElement BuildFullHost()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(220) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var timelineHost = BuildTimelineHost();
            Grid.SetRow(timelineHost, 0);
            grid.Children.Add(timelineHost);

            var lowerGrid = new Grid();
            lowerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            lowerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(lowerGrid, 1);

            var treeHost = BuildTreeHost();
            Grid.SetColumn(treeHost, 0);
            lowerGrid.Children.Add(treeHost);

            var details = new ItemsControl();
            details.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ProfilerScreenViewModel.Details)));
            Grid.SetColumn(details, 1);
            lowerGrid.Children.Add(details);

            grid.Children.Add(lowerGrid);
            return grid;
        }

        private UIElement BuildTimelineHost()
        {
            var scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
            };

            var itemsControl = new ItemsControl();
            itemsControl.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ProfilerScreenViewModel.FrameBars)));

            var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
            panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            itemsControl.ItemsPanel = new ItemsPanelTemplate(panelFactory);

            var itemFactory = new FrameworkElementFactory(typeof(Border));
            itemFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 2, 0));
            itemFactory.SetValue(Border.WidthProperty, 8.0);
            itemFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Bottom);
            itemFactory.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(FrameBarViewModel.Height)));
            itemsControl.ItemTemplate = new DataTemplate { VisualTree = itemFactory };

            scrollViewer.Content = itemsControl;
            return scrollViewer;
        }

        private UIElement BuildTreeHost()
        {
            var treeView = new TreeView();
            treeView.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ProfilerScreenViewModel.ThreadItems)));

            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(ThreadTreeItemViewModel.DisplayName)));
            treeView.ItemTemplate = new HierarchicalDataTemplate(typeof(ThreadTreeItemViewModel))
            {
                ItemsSource = new Binding(nameof(ThreadTreeItemViewModel.Children)),
                VisualTree = textFactory,
            };

            var style = new Style(typeof(TreeViewItem));
            style.Setters.Add(new Setter(
                TreeViewItem.IsExpandedProperty,
                new Binding(nameof(ThreadTreeItemViewModel.IsExpanded)) { Mode = BindingMode.TwoWay }));
            treeView.ItemContainerStyle = style;

            return treeView;
        }
    }

    private enum BenchmarkSurface
    {
        TimelineOnly,
        TreeOnly,
        Full,
    }
}
