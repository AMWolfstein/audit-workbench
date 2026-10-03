using System.ComponentModel.DataAnnotations;
using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Handover;
using AuditWorkbench.Domain.Engagements;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages.Companies;

public class DetailsModel : WorkbenchPageModel
{
    private readonly CompanyService _companies;
    private readonly EngagementService _engagements;
    private readonly IClientHandoverPackageService _handover;

    public DetailsModel(CompanyService companies, EngagementService engagements,
        IClientHandoverPackageService handover)
    {
        _companies = companies;
        _engagements = engagements;
        _handover = handover;
    }

    public CompanySummary Company { get; private set; } = default!;

    public IReadOnlyList<EngagementSummary> Engagements { get; private set; } = Array.Empty<EngagementSummary>();

    public IReadOnlyList<PriorYearOption> PriorYearOptions { get; private set; } = Array.Empty<PriorYearOption>();

    [BindProperty]
    public EngagementInput Input { get; set; } = new();

    public class EngagementInput
    {
        [Required(ErrorMessage = "Financial year label is required.")]
        [StringLength(40)]
        public string Label { get; set; } = string.Empty;

        [Required(ErrorMessage = "Period start is required.")]
        public DateOnly PeriodStart { get; set; }

        [Required(ErrorMessage = "Period end is required.")]
        public DateOnly PeriodEnd { get; set; }

        public string Status { get; set; } = EngagementStatus.Draft;

        [Required]
        [StringLength(3, MinimumLength = 3)]
        public string CurrencyCode { get; set; } = "USD";

        [Range(0, 6)]
        public int MinorUnitScale { get; set; } = 2;

        public Guid? PriorEngagementId { get; set; }
    }

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
            return RedirectToPage("Index");
        }
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            var engagementId = await _engagements.CreateAsync(
                new CreateEngagementCommand(
                    id,
                    Input.Label,
                    Input.PeriodStart,
                    Input.PeriodEnd,
                    Input.Status,
                    Input.CurrencyCode,
                    Input.MinorUnitScale,
                    Input.PriorEngagementId),
                cancellationToken);

            ReportSuccess($"Financial year {Input.Label} created as a separate engagement record.");
            return RedirectToPage("/Engagements/Details", new { id = engagementId });
        }
        catch (Exception exception)
        {
            ReportError(exception);
            await LoadAsync(id, cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostExportAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new MemoryStream();
            await _handover.ExportAsync(id, stream, cancellationToken);
            var company = await _companies.GetAsync(id, cancellationToken);
            var safeName = string.Concat(company.ShortName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
            return File(stream.ToArray(), "application/vnd.auditworkbench.client+zip", $"{safeName}.awb");
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return RedirectToPage(new { id });
        }
    }

    private async Task LoadAsync(Guid companyId, CancellationToken cancellationToken)
    {
        Company = await _companies.GetAsync(companyId, cancellationToken);
        Engagements = await _engagements.ListByCompanyAsync(companyId, cancellationToken);
        PriorYearOptions = await _engagements.EligiblePriorYearsAsync(companyId, cancellationToken: cancellationToken);
    }
}
