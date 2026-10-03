using System.ComponentModel.DataAnnotations;
using AuditWorkbench.Application.Handover;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages;

public sealed class ImportClientModel : WorkbenchPageModel
{
    private readonly IClientHandoverPackageService _handover;
    public ImportClientModel(IClientHandoverPackageService handover) => _handover = handover;

    [BindProperty, Required]
    public IFormFile? Package { get; set; }

    [BindProperty]
    public bool ConfirmImport { get; set; }

    public ClientHandoverValidationReport? Report { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Package is null || Package.Length == 0)
        {
            ModelState.AddModelError(nameof(Package), "Choose an .awb package.");
            return Page();
        }
        if (Package.Length > 100L * 1024 * 1024)
        {
            ModelState.AddModelError(nameof(Package), "The package exceeds the 100 MiB limit.");
            return Page();
        }

        await using var upload = new MemoryStream();
        await Package.CopyToAsync(upload, cancellationToken);
        upload.Position = 0;
        Report = await _handover.ValidateAsync(upload, cancellationToken);
        if (!Report.IsValid || !ConfirmImport) return Page();

        try
        {
            upload.Position = 0;
            var result = await _handover.ImportAsync(upload, cancellationToken);
            ReportSuccess($"Client imported. Import reference: {result.ImportId:D}.");
            return RedirectToPage("/Companies/Details", new { id = result.CompanyId });
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return Page();
        }
    }
}
