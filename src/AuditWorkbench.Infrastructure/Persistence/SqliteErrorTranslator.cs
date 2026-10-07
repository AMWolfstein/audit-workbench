using AuditWorkbench.Domain.Common;
using Microsoft.Data.Sqlite;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>
/// Turns a database guard abort into the matching application exception so the
/// UI shows a clear, safe message instead of a raw SQLite error (ADR-007).
/// </summary>
public static class SqliteErrorTranslator
{
    public const string GuardPrefix = "AWB-GUARD-";

    public static Exception Translate(Exception exception)
    {
        var sqliteException = Find(exception);
        if (sqliteException is null)
        {
            return exception;
        }

        var message = sqliteException.Message;
        var guardIndex = message.IndexOf(GuardPrefix, StringComparison.Ordinal);
        if (guardIndex < 0)
        {
            if (message.Contains("audit_event.sequence_no", StringComparison.Ordinal))
                return new ConcurrencyException(
                    "Another audit event was committed concurrently. Retry the operation.");
            return message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)
                ? new ValidationException("That record already exists in this workspace.")
                : message.Contains("FOREIGN KEY constraint failed", StringComparison.Ordinal)
                    ? new ValidationException("The record refers to something that does not exist in this workspace.")
                    : exception;
        }

        var guard = message[guardIndex..];
        var separator = guard.IndexOf(": ", StringComparison.Ordinal);
        var detail = separator >= 0 ? guard[(separator + 2)..] : guard;
        var code = separator >= 0 ? guard[..separator] : guard;

        if (code.Contains("FINALIZED", StringComparison.Ordinal) ||
            code.Contains("APPEND-ONLY", StringComparison.Ordinal))
        {
            return new EngagementFinalizedException(Capitalise(detail), exception);
        }

        return new IntegrityGuardException(Capitalise(detail), exception);
    }

    public static bool IsGuardViolation(Exception exception) =>
        Find(exception)?.Message.Contains(GuardPrefix, StringComparison.Ordinal) == true;

    private static SqliteException? Find(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is SqliteException sqlite)
            {
                return sqlite;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
