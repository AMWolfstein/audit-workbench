using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuditWorkbench.Infrastructure.Persistence;

public enum AuditDatabaseProvider { PostgreSql, SqlServer, Sqlite }

public sealed record AuditDatabaseOptions(AuditDatabaseProvider Provider, string ConnectionString)
{
    public void Validate(bool production)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("A database connection string is required.");
        if (production && Provider == AuditDatabaseProvider.Sqlite)
            throw new InvalidOperationException("SQLite is for tests/local development, not team production.");
    }
}

/// <summary>Provider selection belongs to infrastructure/composition, never to domain entities.</summary>
public static class DatabaseProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAuditDatabase(this IServiceCollection services, AuditDatabaseOptions database,
        bool production = false)
    {
        database.Validate(production);
        services.AddSingleton(database);
        services.AddSingleton<SqliteConnectionPolicyInterceptor>();
        services.AddDbContext<AuditWorkbenchDbContext>((provider, options) =>
        {
            switch (database.Provider)
            {
                case AuditDatabaseProvider.PostgreSql:
                    options.UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure());
                    break;
                case AuditDatabaseProvider.SqlServer:
                    options.UseSqlServer(database.ConnectionString, sql => sql.EnableRetryOnFailure());
                    break;
                case AuditDatabaseProvider.Sqlite:
                    options.UseSqlite(database.ConnectionString);
                    options.AddInterceptors(provider.GetRequiredService<SqliteConnectionPolicyInterceptor>());
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(database.Provider));
            }
        });
        return services;
    }
}
