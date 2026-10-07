using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Domain.Identity;
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

/// <summary>
/// Client-level policy. A company is visible only through an explicit engagement
/// membership, except that its creator may create the company's first engagement.
/// This closes the gap between the company and engagement authorization boundaries.
/// </summary>
public sealed class CompanyAuthorizationService
{
    private readonly AuditWorkbenchDbContext _db;
    private readonly ICurrentActor _actor;

    public CompanyAuthorizationService(AuditWorkbenchDbContext db, ICurrentActor actor)
    {
        _db = db;
        _actor = actor;
    }

    public IQueryable<Guid> PermittedCompanyIds(string permission = Permissions.ViewEngagement) =>
        from company in _db.Companies
        where _actor.IsAuthenticated && _actor.UserId != Guid.Empty
              && _db.Users.Any(u => u.UserId == _actor.UserId && u.Status == "ACTIVE")
              && ((company.CreatedBy == _actor.UserId
                   && !_db.Engagements.Any(e => e.CompanyId == company.CompanyId)) ||
                  _db.Engagements.Any(e => e.CompanyId == company.CompanyId &&
                      _db.EngagementMembers.Any(m => m.EngagementId == e.EngagementId
                          && m.UserId == _actor.UserId && m.Status == "ACTIVE"
                          && _db.RolePermissions.Any(p => p.RoleId == m.RoleId
                              && p.PermissionKey == permission))))
        select company.CompanyId;

    public Task<bool> CanAccessAsync(Guid companyId, string permission = Permissions.ViewEngagement,
        CancellationToken cancellationToken = default) =>
        PermittedCompanyIds(permission).AnyAsync(id => id == companyId, cancellationToken);

    public async Task RequireAccessAsync(Guid companyId, string permission = Permissions.ViewEngagement,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(companyId, permission, cancellationToken).ConfigureAwait(false))
            throw new AuthorizationException("You do not have access to that client.");
    }

    /// <summary>Creating a year requires ownership or MANAGE_TEAM on one of the client's years.</summary>
    public Task RequireManagementAsync(Guid companyId, CancellationToken cancellationToken = default) =>
        RequireAccessAsync(companyId, Permissions.ManageTeam, cancellationToken);
}

/// <summary>Company master data use cases (FR-M01, FR-M02).</summary>
public sealed class CompanyService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;
    private readonly EngagementAuthorizationService _engagementAuthorization;
    private readonly CompanyAuthorizationService _companyAuthorization;

    public CompanyService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        IClock clock,
        ICurrentActor actor,
        EngagementAuthorizationService engagementAuthorization,
        CompanyAuthorizationService companyAuthorization)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _clock = clock;
        _actor = actor;
        _engagementAuthorization = engagementAuthorization;
        _companyAuthorization = companyAuthorization;
    }

    public Task<Guid> CreateAsync(CreateCompanyCommand command, CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            // Client provisioning is a workspace-level operation. The empty workspace
            // exception allows first-run bootstrap, but an ordinary member cannot add clients.
            await _engagementAuthorization.RequireWorkspacePrivilegeAsync(token).ConfigureAwait(false);
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
            .Where(c => _companyAuthorization.PermittedCompanyIds(Permissions.ViewEngagement).Contains(c.CompanyId))
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
        await _companyAuthorization.RequireAccessAsync(companyId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var company = (await ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.CompanyId == companyId);
        return company ?? throw new NotFoundException("That company does not exist in this workspace.");
    }

}

/// <summary>Status literals usable inside EF Core expression trees.</summary>
internal static class EngagementStatusConstants
{
    public const string Finalized = "FINALIZED";
}
