namespace TraceViewer.SessionModel;

public sealed class MetadataStore
{
    private readonly List<MetadataSpecDefinition> _specs = [];
    private readonly List<MetadataInstance> _instances = [];

    public IReadOnlyList<MetadataSpecDefinition> Specs => _specs;

    public IReadOnlyList<MetadataInstance> Instances => _instances;

    public int AddSpec(string? format, IReadOnlyList<string> fieldNames)
    {
        for (var index = 0; index < _specs.Count; index++)
        {
            var existing = _specs[index];
            if (string.Equals(existing.Format, format, StringComparison.Ordinal) &&
                existing.FieldNames.SequenceEqual(fieldNames, StringComparer.Ordinal))
            {
                return existing.Id;
            }
        }

        var spec = new MetadataSpecDefinition
        {
            Id = _specs.Count,
            Format = format,
            FieldNames = fieldNames.ToArray(),
        };

        _specs.Add(spec);
        return spec.Id;
    }

    public int AddInstance(int originalTimerId, ReadOnlyMemory<byte> payload)
    {
        var instance = new MetadataInstance
        {
            Id = _instances.Count,
            OriginalTimerId = originalTimerId,
            Payload = payload,
        };

        _instances.Add(instance);
        return instance.Id;
    }

    public MetadataInstance? GetInstance(int id)
    {
        return id >= 0 && id < _instances.Count ? _instances[id] : null;
    }

    public void UpdateInstance(int id, int originalTimerId, ReadOnlyMemory<byte> payload)
    {
        var instance = GetInstance(id) ?? throw new ArgumentOutOfRangeException(nameof(id));
        instance.OriginalTimerId = originalTimerId;
        instance.Payload = payload;
    }
}
