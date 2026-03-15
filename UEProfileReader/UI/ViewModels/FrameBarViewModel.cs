using UEProfileReader.SessionModel;

namespace UEProfileReader.UI.ViewModels;

public sealed class FrameBarViewModel : ViewModelBase
{
    private bool _isSelected;

    public required FrameInfo Frame { get; init; }

    public required double Height { get; init; }

    public required string Tooltip { get; init; }

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
}
