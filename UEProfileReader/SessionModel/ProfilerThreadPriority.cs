namespace UEProfileReader.SessionModel;

public enum ProfilerThreadPriority
{
    GameThread = -2,
    TimeCritical = 0,
    Highest = 1,
    AboveNormal = 2,
    Normal = 3,
    SlightlyBelowNormal = 4,
    BelowNormal = 5,
    Lowest = 6,
    Unknown = int.MaxValue,
}
