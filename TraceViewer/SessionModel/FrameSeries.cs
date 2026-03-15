namespace TraceViewer.SessionModel;

public sealed class FrameSeries
{
    private readonly List<FrameInfo> _frames = [];
    private readonly List<double> _startTimes = [];

    public FrameSeries(FrameType frameType)
    {
        FrameType = frameType;
    }

    public FrameType FrameType { get; }

    public IReadOnlyList<FrameInfo> Frames => _frames;

    public IReadOnlyList<double> StartTimes => _startTimes;

    public FrameInfo BeginFrame(double time)
    {
        var frame = new FrameInfo
        {
            Index = (ulong)_frames.Count,
            FrameType = FrameType,
            StartTime = time,
            EndTime = double.PositiveInfinity,
        };

        _frames.Add(frame);
        _startTimes.Add(time);
        return frame;
    }

    public void EndFrame(double time)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        _frames[^1].EndTime = time;
    }

    public FrameInfo? GetFrame(ulong index)
    {
        return index < (ulong)_frames.Count ? _frames[(int)index] : null;
    }

    public bool TryGetFrameFromTime(double time, out FrameInfo? frame)
    {
        frame = null;
        if (_frames.Count == 0)
        {
            return false;
        }

        var lowerBound = LowerBound(_startTimes, time);
        if (lowerBound <= 0)
        {
            return false;
        }

        frame = _frames[lowerBound - 1];
        return true;
    }

    public uint GetFrameNumberForTimestamp(double timestamp)
    {
        if (_frames.Count == 0)
        {
            return 0;
        }

        var lowerBound = LowerBound(_startTimes, timestamp);
        return lowerBound <= 0 ? 0u : (uint)(lowerBound - 1);
    }

    public IReadOnlyList<FrameInfo> EnumerateIntersecting(double startTime, double endTime)
    {
        if (_frames.Count == 0)
        {
            return Array.Empty<FrameInfo>();
        }

        var startIndex = LowerBound(_startTimes, startTime);
        if (startIndex > 0 && _frames[startIndex - 1].EndTime > startTime)
        {
            startIndex--;
        }

        if (startIndex >= _frames.Count)
        {
            return Array.Empty<FrameInfo>();
        }

        var result = new List<FrameInfo>();
        for (var index = startIndex; index < _frames.Count; index++)
        {
            var frame = _frames[index];
            if (frame.StartTime > endTime)
            {
                break;
            }

            result.Add(frame);
        }

        return result;
    }

    private static int LowerBound(IReadOnlyList<double> values, double target)
    {
        var low = 0;
        var high = values.Count;

        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (values[mid] < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
