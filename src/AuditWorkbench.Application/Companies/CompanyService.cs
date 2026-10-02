using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Companies;

public sealed record CreateCompanyCommand(
    string LegalName,
    string ShortName,
    string Industry,
    string CountryCode,
    string? TaxReference);

public sealed class CompanySummary
{
    public required Guid CompanyId { get; init; }

    public required string LegalName { get; init; }

    public required string ShortName { get; init; }

    public required string Industry { get; init; }

    public required string CountryCode { get; init; }

    public string? TaxReference { get; init; }

    public required string Status { get; init; }

    public required string CreatedAtUtc { get; init; }

    public required int EngagementCount { get; init; }

    public required int FinalizedEngagementCount { get; init; }
}

/// <summary>Company master data use cases (FR-M01, FR-M02).</summary>
public sealed class CompanyService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;

    public CompanyService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        IClock clock,
        ICurrentActor actor)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _clock = clock;
        _actor = actor;
    }

    public Task<Guid> CreateAsync(CreateCompanyCommand command, CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            var shortName = (command.ShortName ?? string.Empty).Trim();
            var duplicate = await _dbContext.Companies
                .AnyAsync(c => c.ShortName.ToLower() == shortName.ToLower(), token)
                .ConfigureAwait(false);
            if (duplicate)
            {
                throw new ValidationException($"Short name '{shortName}' is already used by another company.");
            }

            var company = Company.Create(
                Guid.NewGuid(),
                command.LegalName,
                shortName,
                command.Industry,
                command.CountryCode,
                command.TaxReference,
                IClock.Format(_clock.UtcNow),
                _actor.UserId);

            _dbContext.Companies.Add(company);

            await _auditTrail.AppendAsync(
                    AuditEventType.CompanyCreated,
                    AuditEntityType.Company,
                    company.CompanyId.ToString("D"),
                    $"Company '{company.LegalName}' created.",
                    companyId: company.CompanyId,
                    details: AuditDetails.Empty()
                        .With("short_name", company.ShortName)
                        .With("country_code", company.CountryCode),
                    cancellationToken: token)
                .ConfigureAwait(false);

            return company.CompanyId;
        }, cancellationToken);

    public async Task<IReadOnlyList<CompanySummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.Companies
            .AsNoTracking()
            .OrderBy(c => c.LegalName)
            .Select(c => new CompanySummary
            {
                CompanyId = c.CompanyId,
                LegalName = c.LegalName,
                ShortName = c.ShortName,
                Industry = c.Industry,
                CountryCode = c.CountryCode,
                TaxReference = c.TaxReference,
                Status = c.Status,
                CreatedAtUtc = c.CreatedAtUtc,
                EngagementCount = _dbContext.Engagements.Count(e => e.CompanyId == c.CompanyId),
                FinalizedEngagementCount = _dbContext.Engagements
                    .Count(e => e.CompanyId == c.CompanyId && e.Status == EngagementStatusConstants.Finalized),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<CompanySummary> GetAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var company = (await ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.CompanyId == companyId);
        return company ?? throw new NotFoundException("That company does not exist in this workspace.");
    }

    public async Task<Company> GetEntityAsync(Guid companyId, CancellationToken cancellationToken = default) =>
        await _dbContext.Companies.FirstOrDefaultAsync(c => c.CompanyId == companyId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new NotFoundException("That company does not exist in this workspace.");
}

/// <summary>Status literals usable inside EF Core expression trees.</summary>
internal static class EngagementStatusConstants
{
    public const string Finalized = "FINALIZED";
}
