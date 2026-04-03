namespace TraceViewer.UI.ViewModels;

public sealed class FrameBudgetGuideViewModel : ViewModelBase
{
    private double _durationSeconds;
    private string _label = string.Empty;
    private double _translateY;
    private double _labelTranslateY;

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (Math.Abs(_durationSeconds - value) < double.Epsilon)
            {
                return;
            }

            _durationSeconds = value;
            OnPropertyChanged();
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal))
            {
                return;
            }

            _label = value;
            OnPropertyChanged();
        }
    }

    public required string Brush { get; init; }

    public double TranslateY
    {
        get => _translateY;
        set
        {
            if (Math.Abs(_translateY - value) < 0.001)
            {
                return;
            }

            _translateY = value;
            OnPropertyChanged();
        }
    }

    public double LabelTranslateY
    {
        get => _labelTranslateY;
        set
        {
            if (Math.Abs(_labelTranslateY - value) < 0.001)
            {
                return;
            }

            _labelTranslateY = value;
            OnPropertyChanged();
        }
    }
}
