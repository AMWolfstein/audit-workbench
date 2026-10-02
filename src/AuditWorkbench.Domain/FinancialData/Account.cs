using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// A year-specific account. The same business account in two years is two
/// account rows; balances are always engagement-owned (data-model.md 4).
/// </summary>
public class Account
{
    private Account()
    {
    }

    public Guid AccountId { get; private set; }

    public Guid EngagementId { get; private set; }

    public string AccountCode { get; private set; } = string.Empty;

    public string AccountName { get; private set; } = string.Empty;

    public string AccountTypeCode { get; private set; } = AccountType.Unclassified;

    public int DisplayOrder { get; private set; }

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public static Account Create(
        Guid accountId,
        Guid engagementId,
        string accountCode,
        string accountName,
        string accountType,
        int displayOrder,
        string createdAtUtc,
        Guid createdBy)
    {
        accountCode = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        accountName = (accountName ?? string.Empty).Trim();
        accountType = string.IsNullOrWhiteSpace(accountType)
            ? AccountType.Unclassified
            : accountType.Trim().ToUpperInvariant();

        if (accountCode.Length == 0)
        {
            throw new ValidationException("Account code is required.");
        }

        if (accountCode.Length > 32)
        {
            throw new ValidationException("Account code must be 32 characters or fewer.");
        }

        if (accountName.Length == 0)
        {
            throw new ValidationException("Account name is required.");
        }

        if (!AccountType.All.Contains(accountType))
        {
            throw new ValidationException($"'{accountType}' is not a supported account type.");
        }

        if (displayOrder < 0)
        {
            throw new ValidationException("Display order must not be negative.");
        }

        return new Account
        {
            AccountId = accountId,
            EngagementId = engagementId,
            AccountCode = accountCode,
            AccountName = accountName,
            AccountTypeCode = accountType,
            DisplayOrder = displayOrder,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = createdBy,
        };
    }
}
