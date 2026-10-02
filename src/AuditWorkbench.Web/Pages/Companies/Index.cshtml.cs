using System.ComponentModel.DataAnnotations;
using AuditWorkbench.Application.Companies;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages.Companies;

public class IndexModel : WorkbenchPageModel
{
    private readonly CompanyService _companies;

    public IndexModel(CompanyService companies)
    {
        _companies = companies;
    }

    public IReadOnlyList<CompanySummary> Items { get; private set; } = Array.Empty<CompanySummary>();

    [BindProperty]
    public CompanyInput Input { get; set; } = new();

    public class CompanyInput
    {
        [Required(ErrorMessage = "Legal name is required.")]
        [StringLength(200)]
        public string LegalName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Short name is required.")]
        [StringLength(40)]
        public string ShortName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Industry is required.")]
        [StringLength(100)]
        public string Industry { get; set; } = string.Empty;

        [Required(ErrorMessage = "Country is required.")]
        [StringLength(2, MinimumLength = 2, ErrorMessage = "Use the two-letter ISO country code.")]
        public string CountryCode { get; set; } = string.Empty;

        [StringLength(60)]
        public string? TaxReference { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Items = await _companies.ListAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Items = await _companies.ListAsync(cancellationToken);
            return Page();
        }

        try
        {
            var companyId = await _companies.CreateAsync(
                new CreateCompanyCommand(
                    Input.LegalName,
                    Input.ShortName,
                    Input.Industry,
                    Input.CountryCode,
                    Input.TaxReference),
                cancellationToken);

            ReportSuccess($"Company '{Input.LegalName}' created.");
            return RedirectToPage("Details", new { id = companyId });
        }
        catch (Exception exception)
        {
            ReportError(exception);
            Items = await _companies.ListAsync(cancellationToken);
            return Page();
        }
    }
}
