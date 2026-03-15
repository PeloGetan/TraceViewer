namespace UEProfileReader.SessionModel;

public sealed class TimerRegistry
{
    private readonly List<TimerDefinition> _timers =
    [
        new TimerDefinition
        {
            Id = 0,
            Name = "<default>",
            TimerType = TimerType.CpuScope,
        }
    ];

    public IReadOnlyList<TimerDefinition> Timers => _timers;

    public int AddTimer(string name, string? sourceFile, uint sourceLine, TimerType timerType)
    {
        var timer = new TimerDefinition
        {
            Id = _timers.Count,
            Name = name,
            SourceFile = sourceFile,
            SourceLine = sourceLine,
            TimerType = timerType,
        };

        _timers.Add(timer);
        return timer.Id;
    }

    public TimerDefinition? GetTimer(int id)
    {
        return id >= 0 && id < _timers.Count ? _timers[id] : null;
    }

    public void UpdateTimer(int id, string? name = null, string? sourceFile = null, uint? sourceLine = null, int? metadataSpecId = null)
    {
        var timer = GetTimer(id) ?? throw new ArgumentOutOfRangeException(nameof(id));

        if (!string.IsNullOrWhiteSpace(name))
        {
            timer.Name = name;
        }

        if (sourceFile is not null)
        {
            timer.SourceFile = sourceFile;
        }

        if (sourceLine.HasValue)
        {
            timer.SourceLine = sourceLine.Value;
        }

        if (metadataSpecId.HasValue)
        {
            timer.MetadataSpecId = metadataSpecId.Value;
        }
    }
}
