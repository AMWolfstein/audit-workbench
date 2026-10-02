using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Finalization;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages.Engagements;

public class DetailsModel : WorkbenchPageModel
{
    private readonly EngagementService _engagements;
    private readonly FinalizationService _finalization;
    private readonly AuditTrailQuery _auditTrail;

    public DetailsModel(
        EngagementService engagements,
        FinalizationService finalization,
        AuditTrailQuery auditTrail)
    {
        _engagements = engagements;
        _finalization = finalization;
        _auditTrail = auditTrail;
    }

    public EngagementSummary Engagement { get; private set; } = default!;

    public FinalizationPreflight? Preflight { get; private set; }

    public IReadOnlyList<AuditEventRow> Events { get; private set; } = Array.Empty<AuditEventRow>();

    public bool DigestVerified { get; private set; }

    public string ConfirmationPhrase => FinalizationService.ConfirmationPhrase(Engagement);

    [BindProperty]
    public string ConfirmationText { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(id, cancellationToken);
            return Page();
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return RedirectToPage("/Companies/Index");
        }
    }

    public async Task<IActionResult> OnPostStatusAsync(Guid id, string status, int rowVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await _engagements.ChangeStatusAsync(id, status, rowVersion, cancellationToken);
            ReportSuccess($"Status changed to {status}.");
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostFinalizeAsync(Guid id, int rowVersion, CancellationToken cancellationToken)
    {
        try
        {
            var digest = await _finalization.FinalizeAsync(id, ConfirmationText, rowVersion, cancellationToken);
            ReportSuccess($"Financial year finalized and locked. Digest {digest[..16]}...");
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }

        return RedirectToPage(new { id });
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        Engagement = await _engagements.GetAsync(id, cancellationToken);
        Events = await _auditTrail.ListAsync(engagementId: id, limit: 15, cancellationToken: cancellationToken);

        if (Engagement.IsFinalized)
        {
            DigestVerified = await _finalization.VerifyDigestAsync(id, cancellationToken);
        }
        else
        {
            Preflight = await _finalization.PreflightAsync(id, cancellationToken);
        }
    }
}
