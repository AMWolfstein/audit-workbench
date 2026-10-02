using System.Globalization;
using System.Text;
using AuditWorkbench.Domain.Auditing;

namespace AuditWorkbench.Domain.Finalization;

/// <summary>
/// Builds the canonical manifest document specified in
/// docs/finalization-manifest.md. The verification harness implements the same
/// grammar, and tests/fixtures/manifest_v1_example.* freeze the contract.
/// </summary>
public static class FinalizationManifestBuilder
{
    public const string ManifestVersion = "AWB-MANIFEST/1.0";

    public sealed class ManifestInput
    {
        public required Guid EngagementId { get; init; }

        public required Guid CompanyId { get; init; }

        public required string CompanyLegalName { get; init; }

        public required string CompanyShortName { get; init; }

        public required Guid FinancialYearId { get; init; }

        public required string FinancialYearLabel { get; init; }

        public required string PeriodStart { get; init; }

        public required string PeriodEnd { get; init; }

        public required string CurrencyCode { get; init; }

        public required int MinorUnitScale { get; init; }

        public Guid? PriorEngagementId { get; init; }

        public string? PriorRootDigest { get; init; }

        public required IReadOnlyList<ManifestAccountLine> Accounts { get; init; }
    }

    public static string BuildDocument(ManifestInput input)
    {
        var ordered = input.Accounts
            .OrderBy(a => a.AccountCode, StringComparer.Ordinal)
            .ToList();
        var valueCount = ordered.Count(a => a.RevisionNo is not null);

        var builder = new StringBuilder();
        void Line(string value) => builder.Append(value).Append('\n');

        Line(ManifestVersion);
        Line($"engagement_id={input.EngagementId:D}");
        Line($"company_id={input.CompanyId:D}");
        Line($"company_legal_name={Escape(input.CompanyLegalName)}");
        Line($"company_short_name={Escape(input.CompanyShortName)}");
        Line($"financial_year_id={input.FinancialYearId:D}");
        Line($"financial_year_label={Escape(input.FinancialYearLabel)}");
        Line($"period_start={input.PeriodStart}");
        Line($"period_end={input.PeriodEnd}");
        Line($"currency_code={input.CurrencyCode}");
        Line($"minor_unit_scale={input.MinorUnitScale.ToString(CultureInfo.InvariantCulture)}");
        Line($"prior_engagement_id={(input.PriorEngagementId is null ? "NONE" : input.PriorEngagementId.Value.ToString("D"))}");
        Line($"prior_root_digest={input.PriorRootDigest ?? "NONE"}");
        Line($"account_count={ordered.Count.ToString(CultureInfo.InvariantCulture)}");
        Line($"value_count={valueCount.ToString(CultureInfo.InvariantCulture)}");

        foreach (var account in ordered)
        {
            var revision = account.RevisionNo is null
                ? "NONE"
                : account.RevisionNo.Value.ToString(CultureInfo.InvariantCulture);
            var amount = account.AmountMinor is null
                ? "NONE"
                : account.AmountMinor.Value.ToString(CultureInfo.InvariantCulture);
            Line($"account|{Escape(account.AccountCode)}|{Escape(account.AccountName)}|{account.AccountType}|{revision}|{amount}");
        }

        Line("END");
        return builder.ToString();
    }

    public static string ComputeDigest(string document) => AuditEvent.Sha256Hex(document);

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\p", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);
}
