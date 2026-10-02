using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.DemoData;

public sealed class DemoDataResult
{
    public required Guid CompanyId { get; init; }

    public required Guid PriorEngagementId { get; init; }

    public required Guid CurrentEngagementId { get; init; }

    public required string PriorDigest { get; init; }
}

/// <summary>
/// Creates the clearly separated synthetic demonstration dataset (ADR-012).
/// Values mirror tools/verification/demo_data.py so the application, the tests
/// and the documentation all describe the same workspace. Nothing here is or
/// may become real client data.
/// </summary>
public sealed class DemoDataSeeder
{
    public const string DemoCompanyLegalName = "ABC Manufacturing (Demo) Limited";
    public const string DemoCompanyShortName = "ABC-DEMO";
    public const string DemoCompanyIndustry = "Manufacturing";

    /// <summary>"ZZ" is a reserved ISO 3166 code: never a real jurisdiction.</summary>
    public const string DemoCompanyCountry = "ZZ";

    public const string DemoCompanyTaxReference = "DEMO-TAX-0000001";

    private static readonly (string Code, string Name, string Type)[] DemoAccounts =
    {
        ("4000", "Revenue", AccountType.Income),
        ("1200", "Trade receivables", AccountType.Asset),
        ("1300", "Inventory", AccountType.Asset),
    };

    private static readonly Dictionary<string, string> PriorYearValues = new()
    {
        ["4000"] = "850000000",
        ["1200"] = "180000000",
        ["1300"] = "240000000",
    };

    private static readonly Dictionary<string, string> CurrentYearValues = new()
    {
        ["4000"] = "920000000",
        ["1200"] = "210000000",
        ["1300"] = "275000000",
    };

    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly CompanyService _companies;
    private readonly EngagementService _engagements;
    private readonly FinancialDataService _financialData;
    private readonly FinalizationService _finalization;

    public DemoDataSeeder(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        CompanyService companies,
        EngagementService engagements,
        FinancialDataService financialData,
        FinalizationService finalization)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _companies = companies;
        _engagements = engagements;
        _financialData = financialData;
        _finalization = finalization;
    }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) =>
        _dbContext.Companies.AnyAsync(c => c.ShortName == DemoCompanyShortName, cancellationToken);

    public async Task<DemoDataResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ValidationException(
                "The demo dataset already exists in this workspace. Use a new workspace folder for a clean demonstration.");
        }

        var companyId = await _companies.CreateAsync(
                new CreateCompanyCommand(
                    DemoCompanyLegalName,
                    DemoCompanyShortName,
                    DemoCompanyIndustry,
                    DemoCompanyCountry,
                    DemoCompanyTaxReference),
                cancellationToken)
            .ConfigureAwait(false);

        var priorId = await _engagements.CreateAsync(
                new CreateEngagementCommand(
                    companyId,
                    "FY2026",
                    new DateOnly(2026, 1, 1),
                    new DateOnly(2026, 12, 31),
                    EngagementStatus.Draft,
                    "USD",
                    2,
                    null),
                cancellationToken)
            .ConfigureAwait(false);
        await SeedYearAsync(priorId, PriorYearValues, cancellationToken).ConfigureAwait(false);

        var priorSummary = await _engagements.GetAsync(priorId, cancellationToken).ConfigureAwait(false);
        var digest = await _finalization.FinalizeAsync(
                priorId,
                FinalizationService.ConfirmationPhrase(priorSummary),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var currentId = await _engagements.CreateAsync(
                new CreateEngagementCommand(
                    companyId,
                    "FY2027",
                    new DateOnly(2027, 1, 1),
                    new DateOnly(2027, 12, 31),
                    EngagementStatus.Draft,
                    "USD",
                    2,
                    priorId),
                cancellationToken)
            .ConfigureAwait(false);
        await SeedYearAsync(currentId, CurrentYearValues, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.ExecuteAsync(token => _auditTrail.AppendAsync(
                AuditEventType.DemoDataSeeded,
                AuditEntityType.Company,
                companyId.ToString("D"),
                "Synthetic demo dataset 'ABC Manufacturing (Demo)' seeded.",
                companyId: companyId,
                details: AuditDetails.Empty()
                    .With("prior_engagement_id", priorId)
                    .With("current_engagement_id", currentId)
                    .With("synthetic", true),
                cancellationToken: token), cancellationToken)
            .ConfigureAwait(false);

        return new DemoDataResult
        {
            CompanyId = companyId,
            PriorEngagementId = priorId,
            CurrentEngagementId = currentId,
            PriorDigest = digest,
        };
    }

    private async Task SeedYearAsync(
        Guid engagementId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        foreach (var (code, name, type) in DemoAccounts)
        {
            await _financialData.AddAccountAsync(
                    new AddAccountCommand(engagementId, code, name, type, values[code]),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
