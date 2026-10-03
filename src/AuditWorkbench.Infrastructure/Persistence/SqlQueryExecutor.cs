using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>
/// Runs the shared read queries from /db/sql on the DbContext connection,
/// enlisting in the ambient transaction when one is open.
/// </summary>
public sealed class SqlQueryExecutor
{
    private readonly AuditWorkbenchDbContext _dbContext;

    public SqlQueryExecutor(AuditWorkbenchDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string queryFileName,
        IReadOnlyDictionary<string, object?> parameters,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken = default)
    {
        var connection = (SqliteConnection)_dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = SqlResources.Query(queryFileName);
        if (_dbContext.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
        }

        foreach (var (name, value) in parameters)
        {
            // The shared .sql files use ":name" placeholders; Microsoft.Data.Sqlite
            // resolves an unprefixed parameter name against ":", "@" and "$".
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map((SqliteDataReader)reader));
        }

        return results;
    }

    public static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static long? GetNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public static int? GetNullableInt32(SqliteDataReader reader, string column)
    {
        var value = GetNullableInt64(reader, column);
        return value is null ? null : (int)value.Value;
    }

    public static Guid? GetNullableGuid(SqliteDataReader reader, string column)
    {
        var value = GetNullableString(reader, column);
        return value is null ? null : Guid.Parse(value);
    }
}
