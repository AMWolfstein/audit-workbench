using System.ComponentModel.DataAnnotations;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Domain.FinancialData;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages.Engagements;

public class FinancialDataModel : WorkbenchPageModel
{
    private readonly EngagementService _engagements;
    private readonly FinancialDataService _financialData;

    public FinancialDataModel(EngagementService engagements, FinancialDataService financialData)
    {
        _engagements = engagements;
        _financialData = financialData;
    }

    public EngagementSummary Engagement { get; private set; } = default!;

    public IReadOnlyList<FinancialValueRow> Values { get; private set; } = Array.Empty<FinancialValueRow>();

    public IReadOnlyList<FinancialValueHistoryRow> History { get; private set; } =
        Array.Empty<FinancialValueHistoryRow>();

    public Guid? HistoryAccountId { get; private set; }

    public IReadOnlyList<string> AccountTypes => AccountType.All;

    [BindProperty]
    public NewAccountInput NewAccount { get; set; } = new();

    public class NewAccountInput
    {
        [Required(ErrorMessage = "Account code is required.")]
        [StringLength(32)]
        public string AccountCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Account name is required.")]
        [StringLength(120)]
        public string AccountName { get; set; } = string.Empty;

        public string AccountType { get; set; } = Domain.FinancialData.AccountType.Unclassified;

        public string? Amount { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid? historyAccountId, CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(id, cancellationToken);
            if (historyAccountId is { } accountId)
            {
                HistoryAccountId = accountId;
                History = await _financialData.GetHistoryAsync(id, accountId, cancellationToken);
            }

            return Page();
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return RedirectToPage("/Companies/Index");
        }
    }

    public async Task<IActionResult> OnPostAddAccountAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            await _financialData.AddAccountAsync(
                new AddAccountCommand(id, NewAccount.AccountCode, NewAccount.AccountName,
                    NewAccount.AccountType, NewAccount.Amount),
                cancellationToken);
            ReportSuccess($"Account {NewAccount.AccountCode.ToUpperInvariant()} added.");
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSaveValueAsync(
        Guid id,
        Guid accountId,
        string amount,
        string? correctionReason,
        int expectedRevisionNo,
        CancellationToken cancellationToken)
    {
        try
        {
            await _financialData.RecordValueAsync(
                new RecordValueCommand(id, accountId, amount, correctionReason, expectedRevisionNo),
                cancellationToken);
            ReportSuccess("Value saved as a new revision; earlier revisions are kept.");
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
        Values = await _financialData.GetLatestValuesAsync(id, cancellationToken);
    }

    public string FormatAmount(long? amountMinor) => Format(amountMinor, Engagement.MinorUnitScale);

    public string ToEditString(long? amountMinor) => amountMinor is null
        ? string.Empty
        : AuditWorkbench.Domain.Money.MoneyPolicy.ToEditString(amountMinor.Value, Engagement.MinorUnitScale);
}
