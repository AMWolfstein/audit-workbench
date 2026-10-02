using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
