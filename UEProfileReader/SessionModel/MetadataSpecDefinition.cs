namespace UEProfileReader.SessionModel;

public sealed class MetadataSpecDefinition
{
    public required int Id { get; init; }

    public string? Format { get; init; }

    public IReadOnlyList<string> FieldNames { get; init; } = Array.Empty<string>();
}
