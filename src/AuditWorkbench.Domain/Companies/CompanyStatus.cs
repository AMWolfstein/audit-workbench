namespace AuditWorkbench.Domain.Companies;

public static class CompanyStatus
{
    public const string Active = "ACTIVE";
    public const string Archived = "ARCHIVED";

    public static readonly IReadOnlyList<string> All = new[] { Active, Archived };
}
