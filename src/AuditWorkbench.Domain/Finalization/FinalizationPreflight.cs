namespace AuditWorkbench.Domain.Finalization;

/// <summary>Result of the pre-finalization checks shown before the confirmation step.</summary>
public sealed class FinalizationPreflight
{
    public required IReadOnlyList<string> BlockingProblems { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public bool CanFinalize => BlockingProblems.Count == 0;

    public static FinalizationPreflight From(IEnumerable<string> problems, IEnumerable<string>? warnings = null) => new()
    {
        BlockingProblems = problems.ToList(),
        Warnings = (warnings ?? Enumerable.Empty<string>()).ToList(),
    };
}
