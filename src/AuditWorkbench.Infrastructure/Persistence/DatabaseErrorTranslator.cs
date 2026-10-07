using System.Data.Common;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>
/// Provider boundary for safe persistence errors. Domain/application code never
/// parses PostgreSQL, SQL Server or SQLite messages and callers never receive a
/// raw database exception.
/// </summary>
public static class DatabaseErrorTranslator
{
    public static Exception Translate(Exception exception)
    {
        var sqlite = SqliteErrorTranslator.Translate(exception);
        if (!ReferenceEquals(sqlite, exception))
            return sqlite;

        var database = FindDatabaseException(exception);
        var sqlState = database?.GetType().GetProperty("SqlState")?.GetValue(database)?.ToString();
        var numberValue = database?.GetType().GetProperty("Number")?.GetValue(database);
        var number = numberValue is null ? (int?)null : Convert.ToInt32(numberValue);

        return (sqlState, number) switch
        {
            ("23505", _) or (_, 2601) or (_, 2627) =>
                new ValidationException("That record already exists."),
            ("23503", _) or (_, 547) =>
                new ValidationException("The record refers to an unavailable or incompatible resource."),
            ("23514", _) =>
                new ValidationException("The submitted data violates a required business rule."),
            _ => new IntegrityGuardException(
                "The database refused the operation. Nothing was changed.", exception),
        };
    }

    private static DbException? FindDatabaseException(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is DbException database)
                return database;
            exception = exception.InnerException;
        }
        return null;
    }
}
