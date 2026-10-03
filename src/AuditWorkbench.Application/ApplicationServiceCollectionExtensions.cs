using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Backup;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Comparison;
using AuditWorkbench.Application.Dashboard;
using AuditWorkbench.Application.DemoData;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Application.Handover;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Infrastructure.Backup;
using AuditWorkbench.Infrastructure.Identity;
using AuditWorkbench.Infrastructure.Persistence;
using AuditWorkbench.Infrastructure.Time;
using AuditWorkbench.Infrastructure.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuditWorkbench.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole modular monolith against one local workspace folder.
    /// </summary>
    public static IServiceCollection AddAuditWorkbench(this IServiceCollection services, WorkspacePaths paths,
        ICurrentActor? currentActor = null)
    {
        paths.EnsureCreated();

        services.AddSingleton(paths);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ICurrentActor>(currentActor ?? new LocalActor());
        services.AddAuditDatabase(
            new AuditDatabaseOptions(AuditDatabaseProvider.Sqlite, paths.ConnectionString));

        services.AddScoped<UnitOfWork>();
        services.AddScoped<SqlQueryExecutor>();
        services.AddScoped<AuditTrailWriter>();
        services.AddScoped<AuditTrailQuery>();
        services.AddScoped<RejectionAuditor>();
        services.AddScoped<EngagementAuthorizationService>();
        services.AddScoped<TeamService>();
        services.AddScoped<CompanyService>();
        services.AddScoped<EngagementService>();
        services.AddScoped<FinancialDataService>();
        services.AddScoped<ComparisonService>();
        services.AddScoped<FinalizationService>();
        services.AddScoped<IClientHandoverPackageService, ClientHandoverPackageService>();
        services.AddScoped<SqliteBackupWriter>();
        services.AddScoped<BackupService>();
        services.AddScoped<DemoDataSeeder>();
        services.AddScoped<DashboardService>();
        services.AddScoped<WorkspaceInitializer>();

        return services;
    }
}
