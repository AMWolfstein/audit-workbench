using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>
/// Runs portable read queries on the configured EF connection and enlists in the
/// current transaction. Provider-specific value normalization is isolated here.
/// </summary>
public sealed class SqlQueryExecutor
{
    private readonly AuditWorkbenchDbContext _dbContext;

    public SqlQueryExecutor(AuditWorkbenchDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string queryFileName,
        IReadOnlyDictionary<string, object?> parameters,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken = default)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = SqlResources.Query(queryFileName);
        if (!_dbContext.Database.IsSqlite())
        {
            foreach (var name in parameters.Keys)
                command.CommandText = command.CommandText.Replace($":{name}", $"@{name}", StringComparison.Ordinal);
        }
        if (_dbContext.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            // SQLite query resources use :name; central ADO.NET providers receive @name.
            parameter.ParameterName = _dbContext.Database.IsSqlite() ? name : $"@{name}";
            parameter.Value = Normalize(value);
            command.Parameters.Add(parameter);
        }

        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(map(reader));
        return results;
    }

    private object Normalize(object? value)
    {
        if (value is null) return DBNull.Value;
        if (_dbContext.Database.IsSqlite())
        {
            if (value is Guid guid) return guid.ToString("D");
        }
        return value;
    }

    public static string? GetNullableString(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));
    }

    public static long? GetNullableInt64(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal));
    }

    public static int? GetNullableInt32(DbDataReader reader, string column)
    {
        var value = GetNullableInt64(reader, column);
        return value is null ? null : checked((int)value.Value);
    }

    public static Guid? GetNullableGuid(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value is Guid guid ? guid : Guid.Parse(Convert.ToString(value)!);
    }
}
