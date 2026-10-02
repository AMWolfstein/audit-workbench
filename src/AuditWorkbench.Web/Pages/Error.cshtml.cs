using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorModel : WorkbenchPageModel
{
    public string? Message { get; private set; }

    public void OnGet()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        Message = feature?.Error switch
        {
            AuditWorkbench.Domain.Common.AuditWorkbenchException known => known.Message,
            _ => "Something went wrong and the operation was cancelled. No data was changed.",
        };
    }
}
