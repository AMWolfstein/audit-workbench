using System.Globalization;
using System.Text;

namespace AuditWorkbench.Domain.Auditing;

/// <summary>
/// Builds the compact, key-sorted JSON stored in <see cref="AuditEvent.DetailsJson"/>.
/// Canonical output keeps event hashes reproducible across runtimes.
/// Values must identify records - never amounts or document content (NFR-14).
/// </summary>
public sealed class AuditDetails
{
    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);

    public static AuditDetails Empty() => new();

    public AuditDetails With(string key, string? value)
    {
        _values[key] = value is null ? "null" : Quote(value);
        return this;
    }

    public AuditDetails With(string key, Guid? value)
    {
        _values[key] = value is null ? "null" : Quote(value.Value.ToString("D"));
        return this;
    }

    public AuditDetails With(string key, long value)
    {
        _values[key] = value.ToString(CultureInfo.InvariantCulture);
        return this;
    }

    public AuditDetails With(string key, bool value)
    {
        _values[key] = value ? "true" : "false";
        return this;
    }

    public string ToJson()
    {
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var pair in _values)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(Quote(pair.Key)).Append(':').Append(pair.Value);
            first = false;
        }

        return builder.Append('}').ToString();
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
