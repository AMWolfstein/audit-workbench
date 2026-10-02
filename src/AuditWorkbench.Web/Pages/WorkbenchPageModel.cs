using AuditWorkbench.Domain.Common;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuditWorkbench.Web.Pages;

/// <summary>
/// Shared error handling: refusals from the application or the database guards
/// are shown as plain messages instead of stack traces, and the page is
/// re-rendered with the data the operator already entered.
/// </summary>
public abstract class WorkbenchPageModel : PageModel
{
    protected void ReportError(Exception exception)
    {
        TempData["Error"] = exception switch
        {
            AuditWorkbenchException known => known.Message,
            _ => "The operation could not be completed. Nothing was changed.",
        };
    }

    protected void ReportSuccess(string message) => TempData["Success"] = message;

    protected static string Format(long? amountMinor, int scale) => MoneyDisplay.Format(amountMinor, scale);
}

public static class MoneyDisplay
{
    public static string Format(long? amountMinor, int scale) =>
        AuditWorkbench.Domain.Money.MoneyPolicy.Format(amountMinor, scale);
}
