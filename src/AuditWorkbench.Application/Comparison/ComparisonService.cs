using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Comparison;
using AuditWorkbench.Infrastructure.Persistence;

namespace AuditWorkbench.Application.Comparison;

public sealed class ComparativeView
{
    public required EngagementSummary Current { get; init; }

    public EngagementSummary? Prior { get; init; }

    public required IReadOnlyList<ComparativeRow> Rows { get; init; }

    public string? Warning { get; init; }

    public bool HasPriorYear => Prior is not null;
}

/// <summary>
/// Read-only comparison between a current engagement and the finalized prior
/// engagement named by its relationship (FR-M08). It never writes to either
/// year: the query is a pure read and no command is issued.
/// </summary>
public sealed class ComparisonService
{
    private readonly EngagementService _engagements;
    private readonly SqlQueryExecutor _queries;

    public ComparisonService(EngagementService engagements, SqlQueryExecutor queries)
    {
        _engagements = engagements;
        _queries = queries;
    }

    public async Task<ComparativeView> GetAsync(Guid currentEngagementId, CancellationToken cancellationToken = default)
    {
        var current = await _engagements.GetAsync(currentEngagementId, cancellationToken).ConfigureAwait(false);
        EngagementSummary? prior = null;
        if (current.PriorEngagementId is { } priorId)
        {
            prior = await _engagements.GetAsync(priorId, cancellationToken).ConfigureAwait(false);
        }

        var rows = await _queries.QueryAsync(
                "comparative_values.sql",
                new Dictionary<string, object?>
                {
                    ["current_engagement_id"] = current.EngagementId.ToString("D"),
                    ["prior_engagement_id"] = prior?.EngagementId.ToString("D"),
                },
                reader => new ComparativeRow
                {
                    AccountCode = reader.GetString(reader.GetOrdinal("account_code")),
                    AccountName = reader.GetString(reader.GetOrdinal("account_name")),
                    PriorAmountMinor = SqlQueryExecutor.GetNullableInt64(reader, "prior_amount_minor"),
                    CurrentAmountMinor = SqlQueryExecutor.GetNullableInt64(reader, "current_amount_minor"),
                    PriorRevisionNo = SqlQueryExecutor.GetNullableInt32(reader, "prior_revision_no"),
                    CurrentRevisionNo = SqlQueryExecutor.GetNullableInt32(reader, "current_revision_no"),
                    IsNewAccount = reader.GetInt64(reader.GetOrdinal("is_new_account")) == 1,
                    IsMissingInCurrent = reader.GetInt64(reader.GetOrdinal("is_missing_in_current")) == 1,
                },
                cancellationToken)
            .ConfigureAwait(false);

        string? warning = null;
        if (prior is null)
        {
            warning = "This financial year has no prior-year relationship, so no comparison is available. " +
                      "A comparison is only ever drawn through an explicit, recorded link.";
        }
        else if (prior.CurrencyCode != current.CurrencyCode)
        {
            warning = $"The years use different currencies ({prior.CurrencyCode} and {current.CurrencyCode}). " +
                      "Differences are not meaningful.";
        }

        return new ComparativeView
        {
            Current = current,
            Prior = prior,
            Rows = rows,
            Warning = warning,
        };
    }
}
