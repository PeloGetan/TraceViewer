namespace TraceViewer.SessionModel;

public sealed class TimerDefinition
{
    public required int Id { get; init; }

    public required string Name { get; set; }

    public string? SourceFile { get; set; }

    public uint SourceLine { get; set; }

    public TimerType TimerType { get; set; }

    public int? MetadataSpecId { get; set; }
}
