using AuditWorkbench.Application.Comparison;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages.Engagements;

public class ComparativeModel : WorkbenchPageModel
{
    private readonly ComparisonService _comparison;

    public ComparativeModel(ComparisonService comparison)
    {
        _comparison = comparison;
    }

    public ComparativeView View { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            View = await _comparison.GetAsync(id, cancellationToken);
            return Page();
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return RedirectToPage("/Companies/Index");
        }
    }

    public string FormatAmount(long? amountMinor) => Format(amountMinor, View.Current.MinorUnitScale);
}
