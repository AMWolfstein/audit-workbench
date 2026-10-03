using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.Money;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.FinancialData;

public sealed record AddAccountCommand(
    Guid EngagementId,
    string AccountCode,
    string AccountName,
    string AccountType,
    string? Amount);

public sealed record RecordValueCommand(
    Guid EngagementId,
    Guid AccountId,
    string Amount,
    string? CorrectionReason,
    int? ExpectedRevisionNo);

public sealed class FinancialValueRow
{
    public required Guid AccountId { get; init; }

    public required string AccountCode { get; init; }

    public required string AccountName { get; init; }

    public required string AccountType { get; init; }

    public required int DisplayOrder { get; init; }

    public long? AmountMinor { get; init; }

    public int? RevisionNo { get; init; }

    public int RevisionCount { get; init; }

    public string? RecordedAtUtc { get; init; }

    public string? RecordedByDisplayName { get; init; }
}

public sealed class FinancialValueHistoryRow
{
    public required int RevisionNo { get; init; }

    public required long AmountMinor { get; init; }

    public required string RecordedAtUtc { get; init; }

    public required string RecordedByDisplayName { get; init; }

    public string? CorrectionReason { get; init; }
}

/// <summary>
/// Year-owned accounts and append-only values (FR-M06, FR-M07). Every command
/// requires an explicit engagement id and refuses finalized years.
/// </summary>
public sealed class FinancialDataService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly EngagementService _engagements;
    private readonly SqlQueryExecutor _queries;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;
    private readonly EngagementAuthorizationService _authorization;

    public FinancialDataService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        EngagementService engagements,
        SqlQueryExecutor queries,
        IClock clock,
        ICurrentActor actor,
        EngagementAuthorizationService authorization)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _engagements = engagements;
        _queries = queries;
        _clock = clock;
        _actor = actor;
        _authorization = authorization;
    }

    public Task<Guid> AddAccountAsync(AddAccountCommand command, CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            await _authorization.RequireAsync(command.EngagementId, Permissions.EditEngagement, token);
            var engagement = await _engagements.LoadAsync(command.EngagementId, token).ConfigureAwait(false);
            var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, token).ConfigureAwait(false);
            engagement.EnsureOpenForEditing(year.Label);

            var code = (command.AccountCode ?? string.Empty).Trim().ToUpperInvariant();
            var duplicate = await _dbContext.Accounts
                .AnyAsync(a => a.EngagementId == engagement.EngagementId && a.AccountCode == code, token)
                .ConfigureAwait(false);
            if (duplicate)
            {
                throw new ValidationException($"Account code '{code}' already exists in {year.Label}.");
            }

            var nextOrder = await _dbContext.Accounts
                .Where(a => a.EngagementId == engagement.EngagementId)
                .Select(a => (int?)a.DisplayOrder)
                .MaxAsync(token)
                .ConfigureAwait(false) ?? 0;

            var account = Account.Create(
                Guid.NewGuid(),
                engagement.EngagementId,
                code,
                command.AccountName,
                command.AccountType,
                nextOrder + 10,
                IClock.Format(_clock.UtcNow),
                _actor.UserId);

            _dbContext.Accounts.Add(account);

            await _auditTrail.AppendAsync(
                    AuditEventType.AccountCreated,
                    AuditEntityType.Account,
                    account.AccountId.ToString("D"),
                    $"Account {account.AccountCode} ({account.AccountName}) added to {year.Label}.",
                    companyId: engagement.CompanyId,
                    engagementId: engagement.EngagementId,
                    details: AuditDetails.Empty().With("account_code", account.AccountCode),
                    cancellationToken: token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(command.Amount))
            {
                await RecordValueCoreAsync(
                        engagement.EngagementId,
                        account.AccountId,
                        command.Amount!,
                        correctionReason: null,
                        expectedRevisionNo: null,
                        token)
                    .ConfigureAwait(false);
            }

            return account.AccountId;
        }, cancellationToken);

    public Task<Guid> RecordValueAsync(RecordValueCommand command, CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(token => RecordValueCoreAsync(
            command.EngagementId,
            command.AccountId,
            command.Amount,
            command.CorrectionReason,
            command.ExpectedRevisionNo,
            token), cancellationToken);

    private async Task<Guid> RecordValueCoreAsync(
        Guid engagementId,
        Guid accountId,
        string amountText,
        string? correctionReason,
        int? expectedRevisionNo,
        CancellationToken cancellationToken)
    {
        await _authorization.RequireAsync(engagementId, Permissions.EditEngagement, cancellationToken);
        var engagement = await _engagements.LoadAsync(engagementId, cancellationToken).ConfigureAwait(false);
        var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, cancellationToken)
            .ConfigureAwait(false);
        engagement.EnsureOpenForEditing(year.Label);

        var account = await _dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountId == accountId && a.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ValidationException("That account does not belong to this financial year.");

        var previous = await _dbContext.FinancialData
            .Where(f => f.EngagementId == engagementId && f.AccountId == accountId)
            .OrderByDescending(f => f.RevisionNo)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expectedRevisionNo is not null && (previous?.RevisionNo ?? 0) != expectedRevisionNo)
        {
            throw new ConcurrencyException(
                "This value changed in another window. Reload the financial data page and try again.");
        }

        var amountMinor = MoneyPolicy.ParseToMinor(amountText, engagement.MinorUnitScale);

        if (previous is not null && previous.AmountMinor == amountMinor)
        {
            // Nothing changed: do not create an empty revision.
            return previous.FinancialDataId;
        }

        var entry = FinancialDataEntry.CreateRevision(
            Guid.NewGuid(),
            account,
            previous,
            amountMinor,
            engagement.CurrencyCode,
            correctionReason,
            IClock.Format(_clock.UtcNow),
            _actor.UserId);

        _dbContext.FinancialData.Add(entry);

        var isCorrection = entry.RevisionNo > 1;
        await _auditTrail.AppendAsync(
                isCorrection ? AuditEventType.FinancialDataChanged : AuditEventType.FinancialDataAdded,
                AuditEntityType.FinancialData,
                entry.FinancialDataId.ToString("D"),
                isCorrection
                    ? $"Value corrected for {account.AccountCode} in {year.Label} (revision {entry.RevisionNo})."
                    : $"Value recorded for {account.AccountCode} in {year.Label} (revision {entry.RevisionNo}).",
                companyId: engagement.CompanyId,
                engagementId: engagement.EngagementId,
                details: AuditDetails.Empty()
                    .With("account_code", account.AccountCode)
                    .With("revision_no", entry.RevisionNo)
                    .With("supersedes_id", entry.SupersedesId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return entry.FinancialDataId;
    }

    /// <summary>Latest revision per account, read through the shared SQL query.</summary>
    public async Task<IReadOnlyList<FinancialValueRow>> GetLatestValuesAsync(
        Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken);
        return await _queries.QueryAsync(
            "latest_financial_values.sql",
            new Dictionary<string, object?> { ["engagement_id"] = engagementId.ToString("D") },
            reader => new FinancialValueRow
            {
                AccountId = Guid.Parse(reader.GetString(reader.GetOrdinal("account_id"))),
                AccountCode = reader.GetString(reader.GetOrdinal("account_code")),
                AccountName = reader.GetString(reader.GetOrdinal("account_name")),
                AccountType = reader.GetString(reader.GetOrdinal("account_type")),
                DisplayOrder = (int)reader.GetInt64(reader.GetOrdinal("display_order")),
                AmountMinor = SqlQueryExecutor.GetNullableInt64(reader, "amount_minor"),
                RevisionNo = SqlQueryExecutor.GetNullableInt32(reader, "revision_no"),
                RevisionCount = (int)reader.GetInt64(reader.GetOrdinal("revision_count")),
                RecordedAtUtc = SqlQueryExecutor.GetNullableString(reader, "recorded_at_utc"),
                RecordedByDisplayName = SqlQueryExecutor.GetNullableString(reader, "recorded_by_display_name"),
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FinancialValueHistoryRow>> GetHistoryAsync(
        Guid engagementId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken);
        return await (
            from value in _dbContext.FinancialData.AsNoTracking()
            join user in _dbContext.Users.AsNoTracking() on value.RecordedBy equals user.UserId
            where value.EngagementId == engagementId && value.AccountId == accountId
            orderby value.RevisionNo descending
            select new FinancialValueHistoryRow
            {
                RevisionNo = value.RevisionNo,
                AmountMinor = value.AmountMinor,
                RecordedAtUtc = value.RecordedAtUtc,
                RecordedByDisplayName = user.DisplayName,
                CorrectionReason = value.CorrectionReason,
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
