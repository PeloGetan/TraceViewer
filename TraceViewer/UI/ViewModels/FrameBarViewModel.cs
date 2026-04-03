using TraceViewer.SessionModel;

namespace TraceViewer.UI.ViewModels;

public sealed class FrameBarViewModel : ViewModelBase
{
    private bool _isSelected;
    private double _height;

    public required FrameInfo Frame { get; init; }

    public required double DurationSeconds { get; init; }

    public double Height
    {
        get => _height;
        set
        {
            if (Math.Abs(_height - value) < 0.001)
            {
                return;
            }

            _height = value;
            OnPropertyChanged();
        }
    }

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
