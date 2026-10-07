using System.Data;
using System.Data.Common;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Infrastructure.FinancialData;

/// <summary>
/// Batched multi-row insert used by the TB/GL importers.
/// <para>
/// Importing hundreds of thousands of rows one statement at a time (or through
/// per-row EF tracking) is not acceptable, so rows are written in multi-row
/// INSERT statements on the existing transaction and connection. The batch size
/// adapts to the provider's parameter limit, and the whole import still runs in
/// the single transaction the caller opened.
/// </para>
/// </summary>
public sealed class BulkInserter
{
    private readonly AuditWorkbenchDbContext _dbContext;

    public BulkInserter(AuditWorkbenchDbContext dbContext) => _dbContext = dbContext;

    /// <summary>Rows written per statement, capped by the provider's parameter limit.</summary>
    public int BatchSize(int columnCount)
    {
        var parameterLimit = _dbContext.Database.IsSqlite() ? 30_000 : _dbContext.Database.IsSqlServer() ? 2_000 : 60_000;
        var byLimit = Math.Max(1, parameterLimit / Math.Max(1, columnCount));
        return Math.Min(byLimit, FinancialBatchUpperBound);
    }

    public async Task<int> InsertAsync<T>(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<T> rows,
        Func<T, object?[]> project,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var prefix = _dbContext.Database.IsSqlite() ? "$" : "@";
        var batchSize = BatchSize(columns.Count);
        var written = 0;

        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, rows.Count - offset);
            await using var command = connection.CreateCommand();
            if (_dbContext.Database.CurrentTransaction is { } transaction)
            {
                command.Transaction = transaction.GetDbTransaction();
            }

            var sql = new System.Text.StringBuilder();
            sql.Append("INSERT INTO ").Append(table).Append(" (")
                .Append(string.Join(", ", columns)).Append(") VALUES ");

            var parameters = new List<DbParameter>(count * columns.Count);
            for (var rowIndex = 0; rowIndex < count; rowIndex++)
            {
                if (rowIndex > 0)
                {
                    sql.Append(", ");
                }

                sql.Append('(');
                var values = project(rows[offset + rowIndex]);
                if (values.Length != columns.Count)
                {
                    throw new InvalidOperationException(
                        $"Bulk insert into {table} expected {columns.Count} values per row but received {values.Length}.");
                }

                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    if (columnIndex > 0)
                    {
                        sql.Append(", ");
                    }

                    var name = $"{prefix}p{rowIndex}_{columnIndex}";
                    sql.Append(name);
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = Normalize(values[columnIndex]);
                    parameters.Add(parameter);
                }

                sql.Append(')');
            }

            sql.Append(';');
            command.CommandText = sql.ToString();
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            written += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private object Normalize(object? value)
    {
        if (value is null)
        {
            return DBNull.Value;
        }

        if (_dbContext.Database.IsSqlite() && value is Guid guid)
        {
            return guid.ToString("D");
        }

        if (value is bool flag)
        {
            return flag ? 1 : 0;
        }

        return value;
    }

    /// <summary>A single batch never exceeds this many rows even on generous providers.</summary>
    private const int FinancialBatchUpperBound = 1000;
}
