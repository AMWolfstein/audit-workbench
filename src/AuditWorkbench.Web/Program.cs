using System.Net;
using AuditWorkbench.Application;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Infrastructure.Workspace;
using AuditWorkbench.Web.Identity;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var workspacePath = builder.Configuration["Workbench:WorkspacePath"];
var paths = string.IsNullOrWhiteSpace(workspacePath)
    ? WorkspacePaths.Default()
    : new WorkspacePaths(workspacePath);

var port = builder.Configuration.GetValue("Workbench:Port", 0);

// NFR-02: bind the loopback interface only. The application is a local desktop
// tool; it never listens on a routable interface and needs no inbound firewall rule.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    options.AddServerHeader = false;
});

builder.Services.AddAuditWorkbench(paths);

// Shared/server deployments select Claims and configure their approved ASP.NET
// authentication handler. Identity is then taken only from the authenticated
// ClaimsPrincipal; the local actor remains explicit development compatibility.
var useClaimsIdentity = string.Equals(
    builder.Configuration["Workbench:IdentityMode"], "Claims", StringComparison.OrdinalIgnoreCase);
if (useClaimsIdentity)
{
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentActor, HttpCurrentActor>();
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization();
}

builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new AutoValidateAntiforgeryTokenAttribute());
});

builder.Services.AddHostFiltering(options =>
{
    options.AllowedHosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };
    options.AllowEmptyHosts = false;
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "awb.antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

var app = builder.Build();

// Apply migrations and verify workspace integrity before serving any page.
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<WorkspaceInitializer>();
    var report = await initializer.InitializeAsync();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    logger.LogInformation(
        "Workspace {Root} ready (schema {Schema}, healthy: {Healthy}).",
        report.WorkspaceRoot, report.SchemaVersion, report.IsHealthy);

    if (!report.IsHealthy)
    {
        foreach (var problem in report.IntegrityProblems)
        {
            logger.LogError("Workspace integrity problem: {Problem}", problem);
        }

        // NFR-05: refuse to open an unsafe workspace rather than guessing.
        throw new InvalidOperationException(
            "The workspace failed its integrity checks and was not opened. Restore a verified backup.");
    }
}

app.UseExceptionHandler("/Error");
app.UseHostFiltering();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
if (useClaimsIdentity)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.MapRazorPages();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;
    var url = addresses?.FirstOrDefault() ?? $"http://127.0.0.1:{port}";
    app.Logger.LogInformation("Audit Workbench is available at {Url}", url);

    if (app.Configuration.GetValue("Workbench:LaunchBrowser", true))
    {
        BrowserLauncher.TryOpen(url, app.Logger);
    }
});

app.Run();

/// <summary>Opens the default browser without requiring elevation.</summary>
internal static class BrowserLauncher
{
    public static void TryOpen(string url, ILogger logger)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not open a browser automatically. Open {Url} manually.", url);
        }
    }
}

/// <summary>Exposed so integration tests can reference the web host.</summary>
public partial class Program
{
}
