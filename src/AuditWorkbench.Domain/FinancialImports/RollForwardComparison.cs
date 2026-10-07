namespace AuditWorkbench.Domain.FinancialImports;

/// <summary>
/// Classification of one transaction when a later snapshot of the same period is
/// compared with the snapshot that was already examined. Later roll-forward work
/// selects newly added and changed transactions for mandatory testing; this
/// phase only establishes the comparison.
/// </summary>
public static class RollForwardState
{
    public const string Unchanged = "UNCHANGED";
    public const string ChangedValue = "CHANGED_VALUE";
    public const string ChangedAttributes = "CHANGED_ATTRIBUTES";
    public const string Added = "ADDED";
    public const string Removed = "REMOVED";

    public static readonly IReadOnlyList<string> All =
        new[] { Unchanged, ChangedValue, ChangedAttributes, Added, Removed };

    /// <summary>Changed or newly added: in a later phase these are eligible for mandatory testing.</summary>
    public static bool IsChangeOrAddition(string state) =>
        state is Added or ChangedValue or ChangedAttributes;

    public static string Describe(string state) => state switch
    {
        Unchanged => "Unchanged",
        ChangedValue => "Amount changed",
        ChangedAttributes => "Attributes changed",
        Added => "Added since the previous snapshot",
        Removed => "Missing from the new snapshot",
        _ => state,
    };
}

/// <summary>Aggregate comparison of two snapshots.</summary>
public sealed class RollForwardSummary
{
    public required Guid EngagementId { get; init; }

    public required string DatasetKind { get; init; }

    public Guid? PreviousImportId { get; init; }

    public Guid? CurrentImportId { get; init; }

    public string? PreviousLabel { get; init; }

    public string? CurrentLabel { get; init; }

    public string? PreviousThroughDate { get; init; }

    public string? CurrentThroughDate { get; init; }

    public int UnchangedCount { get; init; }

    public int ChangedValueCount { get; init; }

    public int ChangedAttributesCount { get; init; }

    public int AddedCount { get; init; }

    public int RemovedCount { get; init; }

    public long AddedDebitMinor { get; init; }

    public long AddedCreditMinor { get; init; }

    public long ChangedValueDeltaMinor { get; init; }

    public long RemovedDebitMinor { get; init; }

    public long RemovedCreditMinor { get; init; }

    public int TotalCompared => UnchangedCount + ChangedValueCount + ChangedAttributesCount + AddedCount + RemovedCount;

    public int ChangeOrAdditionCount => ChangedValueCount + ChangedAttributesCount + AddedCount;

    public bool IsIdentical => ChangeOrAdditionCount == 0 && RemovedCount == 0;

    public int CountOf(string state) => state switch
    {
        RollForwardState.Unchanged => UnchangedCount,
        RollForwardState.ChangedValue => ChangedValueCount,
        RollForwardState.ChangedAttributes => ChangedAttributesCount,
        RollForwardState.Added => AddedCount,
        RollForwardState.Removed => RemovedCount,
        _ => 0,
    };
}

/// <summary>One compared transaction line (previous and/or current state).</summary>
public sealed record RollForwardLineRow(
    string State,
    string JournalIdentity,
    string LineIdentity,
    string? PreviousAccountCode,
    string? CurrentAccountCode,
    long? PreviousDebitMinor,
    long? PreviousCreditMinor,
    long? CurrentDebitMinor,
    long? CurrentCreditMinor,
    string? PreviousTransactionDate,
    string? CurrentTransactionDate,
    string? PreviousDescription,
    string? CurrentDescription,
    string? PreviousJournalSource,
    string? CurrentJournalSource);

/// <summary>Aggregate comparison of two trial-balance versions of one period.</summary>
public sealed record TbVersionDifferenceRow(
    string State,
    string AccountCode,
    string AccountName,
    long? PreviousBalanceMinor,
    long? CurrentBalanceMinor,
    long? PreviousDebitMinor,
    long? PreviousCreditMinor,
    long? CurrentDebitMinor,
    long? CurrentCreditMinor)
{
    public long BalanceDeltaMinor => (CurrentBalanceMinor ?? 0) - (PreviousBalanceMinor ?? 0);
}
