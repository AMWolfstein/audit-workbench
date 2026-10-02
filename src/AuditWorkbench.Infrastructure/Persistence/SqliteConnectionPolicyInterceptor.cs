using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>
/// Applies the safe SQLite settings to every connection (NFR-06). Foreign keys
/// and journal behaviour are not optional extras: the integrity guards depend
/// on them.
/// </summary>
public sealed class SqliteConnectionPolicyInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    public static void Apply(DbConnection connection)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = FULL;");
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
