using AuditWorkbench.Application.Dashboard;
using AuditWorkbench.Application.DemoData;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages;

public class IndexModel : WorkbenchPageModel
{
    private readonly DashboardService _dashboard;
    private readonly DemoDataSeeder _demoData;

    public IndexModel(DashboardService dashboard, DemoDataSeeder demoData)
    {
        _dashboard = dashboard;
        _demoData = demoData;
    }

    public DashboardView View { get; private set; } = default!;

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        View = await _dashboard.GetAsync(cancellationToken);

    public async Task<IActionResult> OnPostSeedDemoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _demoData.SeedAsync(cancellationToken);
            ReportSuccess("Synthetic demo dataset created: ABC Manufacturing (Demo) with a finalized FY2026 " +
                          "and a linked FY2027 draft.");
            return RedirectToPage("/Engagements/Comparative", new { id = result.CurrentEngagementId });
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return RedirectToPage();
        }
    }
}
