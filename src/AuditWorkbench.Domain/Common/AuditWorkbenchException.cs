namespace AuditWorkbench.Domain.Common;

/// <summary>
/// Base class for every refusal the application reports to the operator.
/// Each exception carries a stable code so the UI can show a safe message and
/// the logs can be correlated without reproducing financial content (NFR-14).
/// </summary>
public abstract class AuditWorkbenchException : Exception
{
    protected AuditWorkbenchException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    /// <summary>The engagement the refused operation targeted, when known (used to audit rejections).</summary>
    public Guid? EngagementId { get; init; }
}

/// <summary>Input or business-rule validation failed; nothing was written.</summary>
public sealed class ValidationException : AuditWorkbenchException
{
    public ValidationException(string message)
        : base("AWB-VALIDATION", message)
    {
    }
}

/// <summary>The requested record does not exist in this workspace.</summary>
public sealed class NotFoundException : AuditWorkbenchException
{
    public NotFoundException(string message)
        : base("AWB-NOT-FOUND", message)
    {
    }
}

/// <summary>
/// A write was attempted against a finalized engagement. Raised by the
/// application guard and by translation of the database trigger abort.
/// </summary>
public sealed class EngagementFinalizedException : AuditWorkbenchException
{
    public EngagementFinalizedException(string message, Exception? innerException = null)
        : base("AWB-FINALIZED", message, innerException)
    {
    }
}

/// <summary>A stale browser tab or parallel command lost an optimistic concurrency check.</summary>
public sealed class ConcurrencyException : AuditWorkbenchException
{
    public ConcurrencyException(string message)
        : base("AWB-CONCURRENCY", message)
    {
    }
}

/// <summary>The authenticated user is not a permitted member of the engagement.</summary>
public sealed class AuthorizationException : AuditWorkbenchException
{
    public AuthorizationException(string message) : base("AWB-FORBIDDEN", message) { }
}

/// <summary>A database integrity guard refused the operation.</summary>
public sealed class IntegrityGuardException : AuditWorkbenchException
{
    public IntegrityGuardException(string message, Exception? innerException = null)
        : base("AWB-INTEGRITY", message, innerException)
    {
    }
}
