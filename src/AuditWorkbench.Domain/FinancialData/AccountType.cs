namespace AuditWorkbench.Domain.FinancialData;

public static class AccountType
{
    public const string Asset = "ASSET";
    public const string Liability = "LIABILITY";
    public const string Equity = "EQUITY";
    public const string Income = "INCOME";
    public const string Expense = "EXPENSE";
    public const string Unclassified = "UNCLASSIFIED";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Asset, Liability, Equity, Income, Expense, Unclassified,
    };
}
