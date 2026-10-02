using AuditWorkbench.Domain.Finalization;
using Xunit;

namespace AuditWorkbench.Domain.Tests;

/// <summary>
/// The manifest grammar is a cross-runtime contract (docs/finalization-manifest.md).
/// These fixtures are also asserted by tools/verification/tests/test_schema_contract.py.
/// </summary>
public class FinalizationManifestTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static FinalizationManifestBuilder.ManifestInput ExampleInput() => new()
    {
        EngagementId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
        CompanyId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
        CompanyLegalName = "ABC Manufacturing (Demo) Limited",
        CompanyShortName = "ABC-DEMO",
        FinancialYearId = Guid.Parse("33333333-3333-4333-8333-333333333333"),
        FinancialYearLabel = "FY2026",
        PeriodStart = "2026-01-01",
        PeriodEnd = "2026-12-31",
        CurrencyCode = "USD",
        MinorUnitScale = 2,
        PriorEngagementId = null,
        PriorRootDigest = null,
        Accounts = new List<ManifestAccountLine>
        {
            new() { AccountCode = "4000", AccountName = "Revenue", AccountType = "INCOME",
                RevisionNo = 2, AmountMinor = 85_000_000_000L },
            new() { AccountCode = "1200", AccountName = "Trade receivables", AccountType = "ASSET",
                RevisionNo = 1, AmountMinor = 18_000_000_000L },
            new() { AccountCode = "1300", AccountName = "Inventory", AccountType = "ASSET",
                RevisionNo = 1, AmountMinor = 24_000_000_000L },
            new() { AccountCode = "9999", AccountName = @"Suspense | pipe \ backslash",
                AccountType = "UNCLASSIFIED", RevisionNo = null, AmountMinor = null },
        },
    };

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDirectory, name)).Replace("\r\n", "\n");

    [Fact]
    public void ReproducesTheFrozenFixtureDocument() =>
        Assert.Equal(ReadFixture("manifest_v1_example.txt"), FinalizationManifestBuilder.BuildDocument(ExampleInput()));

    [Fact]
    public void ReproducesTheFrozenFixtureDigest()
    {
        var expected = ReadFixture("manifest_v1_example.sha256").Trim();
        var document = FinalizationManifestBuilder.BuildDocument(ExampleInput());

        Assert.Equal(expected, FinalizationManifestBuilder.ComputeDigest(document));
    }

    [Fact]
    public void DigestIsIndependentOfAccountOrder()
    {
        var input = ExampleInput();
        var reversed = new FinalizationManifestBuilder.ManifestInput
        {
            EngagementId = input.EngagementId,
            CompanyId = input.CompanyId,
            CompanyLegalName = input.CompanyLegalName,
            CompanyShortName = input.CompanyShortName,
            FinancialYearId = input.FinancialYearId,
            FinancialYearLabel = input.FinancialYearLabel,
            PeriodStart = input.PeriodStart,
            PeriodEnd = input.PeriodEnd,
            CurrencyCode = input.CurrencyCode,
            MinorUnitScale = input.MinorUnitScale,
            Accounts = input.Accounts.Reverse().ToList(),
        };

        Assert.Equal(
            FinalizationManifestBuilder.ComputeDigest(FinalizationManifestBuilder.BuildDocument(input)),
            FinalizationManifestBuilder.ComputeDigest(FinalizationManifestBuilder.BuildDocument(reversed)));
    }

    [Fact]
    public void DigestChangesWhenAnAmountChanges()
    {
        var input = ExampleInput();
        var changed = new FinalizationManifestBuilder.ManifestInput
        {
            EngagementId = input.EngagementId,
            CompanyId = input.CompanyId,
            CompanyLegalName = input.CompanyLegalName,
            CompanyShortName = input.CompanyShortName,
            FinancialYearId = input.FinancialYearId,
            FinancialYearLabel = input.FinancialYearLabel,
            PeriodStart = input.PeriodStart,
            PeriodEnd = input.PeriodEnd,
            CurrencyCode = input.CurrencyCode,
            MinorUnitScale = input.MinorUnitScale,
            Accounts = new List<ManifestAccountLine>
            {
                new() { AccountCode = "4000", AccountName = "Revenue", AccountType = "INCOME",
                    RevisionNo = 2, AmountMinor = 85_000_000_001L },
            },
        };

        Assert.NotEqual(
            FinalizationManifestBuilder.ComputeDigest(FinalizationManifestBuilder.BuildDocument(input)),
            FinalizationManifestBuilder.ComputeDigest(FinalizationManifestBuilder.BuildDocument(changed)));
    }

    [Fact]
    public void ManifestRecordStoresAMatchingDigest()
    {
        var document = FinalizationManifestBuilder.BuildDocument(ExampleInput());
        var manifest = FinalizationManifest.Create(
            Guid.NewGuid(), ExampleInput().EngagementId, document, 4, "2027-01-01T00:00:00.000Z", Guid.NewGuid());

        Assert.True(manifest.DigestMatchesContent());
        Assert.Equal(64, manifest.RootDigest.Length);
    }
}
