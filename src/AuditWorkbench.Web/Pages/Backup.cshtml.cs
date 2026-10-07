using AuditWorkbench.Application.Backup;
using AuditWorkbench.Infrastructure.Backup;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages;

public class BackupModel : WorkbenchPageModel
{
    private readonly BackupService _backups;

    public BackupModel(BackupService backups)
    {
        _backups = backups;
    }

    public IReadOnlyList<BackupPackage> Packages { get; private set; } = Array.Empty<BackupPackage>();

    public BackupVerificationResult? Verification { get; private set; }

    public string DefaultDestination => _backups.DefaultDestination;

    [BindProperty]
    public string? DestinationDirectory { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Packages = await _backups.ListAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var package = await _backups.CreateAsync(DestinationDirectory, cancellationToken);
            ReportSuccess($"Backup '{package.Name}' created ({package.SizeBytes / 1024} KB).");
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVerifyAsync(string packageDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            Verification = await _backups.VerifyAsync(packageDirectory, cancellationToken);
            if (Verification.IsValid)
            {
                ReportSuccess($"Backup '{Verification.Name}' verified: checksums and database integrity are correct.");
            }
            else
            {
                TempData["Error"] = $"Backup '{Verification.Name}' is not valid: " +
                                    string.Join(" ", Verification.Problems);
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }

        return RedirectToPage();
    }
}
