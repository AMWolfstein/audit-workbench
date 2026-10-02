using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>Stores GUIDs as canonical lowercase 36-character text.</summary>
public sealed class GuidTextConverter : ValueConverter<Guid, string>
{
    public static readonly GuidTextConverter Instance = new();

    public GuidTextConverter()
        : base(
            value => value.ToString("D").ToLowerInvariant(),
            value => Guid.Parse(value))
    {
    }
}
