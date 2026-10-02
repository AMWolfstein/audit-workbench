using System.Globalization;

namespace AuditWorkbench.Domain.Common;

/// <summary>Abstracted UTC clock so lifecycle behaviour is deterministic in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Canonical stored timestamp: yyyy-MM-ddTHH:mm:ss.fffZ.</summary>
    string NowIso() => Format(UtcNow);

    static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
