using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// The two imported datasets of a period. A trial balance is a balance snapshot
/// (<c>TB Import #2</c>); a general ledger is a transaction population extracted
/// through a date (<c>GL Snapshot #1</c>), which is why the two verbs are kept
/// apart in the UI and in the audit trail (requirements section 6).
/// </summary>
public static class FinancialDatasetKind
{
    public const string TrialBalance = "TB";
    public const string GeneralLedger = "GL";

    public static readonly IReadOnlyList<string> All = new[] { TrialBalance, GeneralLedger };

    public static bool IsValid(string? datasetKind) =>
        datasetKind is TrialBalance or GeneralLedger;

    public static string DisplayName(string datasetKind) => datasetKind == GeneralLedger
        ? "General ledger"
        : "Trial balance";

    public static string VersionNoun(string datasetKind) => datasetKind == GeneralLedger
        ? "Snapshot"
        : "Import";

    public static string Label(string datasetKind, int importNo) => $"{VersionNoun(datasetKind)} #{importNo}";

    public static void EnsureValid(string datasetKind)
    {
        if (!IsValid(datasetKind))
        {
            throw new ValidationException("A financial dataset is either a trial balance (TB) or a general ledger (GL).");
        }
    }
}

/// <summary>
/// Version lifecycle of one imported dataset.
/// <para>
/// DRAFT (header being prepared) -&gt; VALIDATED (rows may be written) -&gt;
/// IMPORTED (rows committed, addressable) -&gt; FINALIZED (evidence, never edited).
/// SUPERSEDED marks a committed version that a later version replaced; it stays
/// readable forever.
/// </para>
/// </summary>
public static class DatasetImportStatus
{
    public const string Draft = "DRAFT";
    public const string Validated = "VALIDATED";
    public const string Imported = "IMPORTED";
    public const string Finalized = "FINALIZED";
    public const string Superseded = "SUPERSEDED";

    public static readonly IReadOnlyList<string> All = new[] { Draft, Validated, Imported, Finalized, Superseded };

    /// <summary>True once rows exist: the dataset is addressable and its content is frozen.</summary>
    public static bool HasRows(string status) => status is Imported or Finalized or Superseded;

    public static bool IsPending(string status) => status is Draft or Validated;

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Draft, Draft or Validated or Imported) => true,
        (Validated, Validated or Imported) => true,
        (Imported, Imported or Finalized or Superseded) => true,
        (Finalized, Finalized or Superseded) => true,
        (Superseded, Superseded) => true,
        _ => false,
    };

    public static string DisplayName(string status) => status switch
    {
        Draft => "Draft",
        Validated => "Validated",
        Imported => "Imported",
        Finalized => "Finalized",
        Superseded => "Superseded",
        _ => status,
    };
}

/// <summary>Result of the validation pass, stored on the import as provenance.</summary>
public static class DatasetValidationStatus
{
    public const string NotRun = "NOT_RUN";
    public const string Valid = "VALID";
    public const string ValidWithWarnings = "VALID_WITH_WARNINGS";
    public const string Rejected = "REJECTED";

    public static string DisplayName(string status) => status switch
    {
        Valid => "Valid",
        ValidWithWarnings => "Valid with warnings",
        Rejected => "Rejected",
        _ => "Not validated",
    };
}

/// <summary>
/// How a transaction identity was obtained: from the client's own stable key, or
/// derived deterministically by the importer because the export carried none.
/// </summary>
public static class ImportIdentitySource
{
    public const string Source = "SOURCE";
    public const string Derived = "DERIVED";

    public static string DisplayName(string source) => source == Derived
        ? "Derived by the importer"
        : "Client transaction reference";
}

/// <summary>
/// Provenance of an account master row. Named in the plural so the vocabulary can
/// never be shadowed by <see cref="Account.AccountOrigin"/> inside the entity.
/// </summary>
public static class AccountOrigins
{
    public const string Manual = "MANUAL";
    public const string TrialBalance = "TB";
    public const string GeneralLedger = "GL";

    public static readonly IReadOnlyList<string> All = new[] { Manual, TrialBalance, GeneralLedger };

    public static string DisplayName(string origin) => origin switch
    {
        TrialBalance => "Trial balance import",
        GeneralLedger => "General ledger import",
        _ => "Manual entry",
    };
}
