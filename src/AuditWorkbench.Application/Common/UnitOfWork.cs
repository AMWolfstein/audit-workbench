using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Common;

/// <summary>
/// One application command equals one database transaction containing the
/// business mutation and its audit event (architecture.md section 7). Database
/// guard violations are translated into application errors before they reach
/// the UI.
/// </summary>
public sealed class UnitOfWork
{
    private readonly AuditWorkbenchDbContext _dbContext;

    public UnitOfWork(AuditWorkbenchDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            // Already inside a caller-managed transaction (for example the
            // demo seeder): join it so atomicity still holds.
            return await RunAsync(action, cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var result = await RunAsync(action, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<T> RunAsync(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyException(
                "The record changed after it was loaded. Reload it and reapply your changes.");
        }
        catch (Exception exception) when (SqliteErrorTranslator.IsGuardViolation(exception))
        {
            throw SqliteErrorTranslator.Translate(exception);
        }
        catch (DbUpdateException exception)
        {
            throw SqliteErrorTranslator.Translate(exception);
        }
    }
}
