using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Win32;
using TraceViewer.UI.ViewModels;

namespace TraceViewer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const string AutoLoadTraceEnvVar = "UEPROFILEREADER_AUTOLOAD_TRACE";

    private bool _isTimelinePanning;
    private Point _lastTimelinePanPoint;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ProfilerScreenViewModel();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilerScreenViewModel viewModel &&
            string.Equals(Environment.GetEnvironmentVariable(AutoLoadTraceEnvVar), "1", StringComparison.Ordinal))
        {
            await viewModel.TryLoadDefaultTraceAsync();
        }
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
            await viewModel.LoadTraceAsync(dialog.FileName);
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

        e.Handled = true;
    }

    private void FrameTimelineScrollViewer_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _isTimelinePanning = true;
        _lastTimelinePanPoint = e.GetPosition(scrollViewer);
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
