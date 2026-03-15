using System.Collections.ObjectModel;
using UEProfileReader.Query;
using UEProfileReader.SessionModel;

namespace UEProfileReader.UI.ViewModels;

public sealed class ThreadTreeItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isExpanded;

    public ThreadInfo? Thread { get; init; }

    public CallTreeNode? Node { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string DurationText { get; init; } = string.Empty;

    public IReadOnlyList<string> HiddenChainNames { get; init; } = Array.Empty<string>();

    public ObservableCollection<ThreadTreeItemViewModel> Children { get; } = [];

    public bool HasHiddenChain => HiddenChainNames.Count > 0;

    public string HiddenChainBadgeText => HasHiddenChain ? $"+{HiddenChainNames.Count}" : string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }
}
