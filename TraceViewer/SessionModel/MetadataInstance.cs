namespace TraceViewer.SessionModel;

public sealed class MetadataInstance
{
    public required int Id { get; init; }

    public required int OriginalTimerId { get; set; }

    public ReadOnlyMemory<byte> Payload { get; set; }
}
