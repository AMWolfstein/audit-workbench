using System.Globalization;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// A year-specific account. The same business account in two years is two
/// account rows; balances are always engagement-owned (data-model.md 4).
/// <para>
/// Account codes are unique <em>per engagement</em>, never globally: two clients
/// may both use "400100". <see cref="NormalizedCode"/> is a derived comparison
/// key (separators removed) used by import duplicate detection and search; it is
/// never a replacement for the client's code, which is preserved verbatim on the
/// imported line.
/// </para>
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

    /// <summary>Comparison key derived from the code: upper case with separators removed.</summary>
    public string? NormalizedCode { get; private set; }

    /// <summary>Client/firm grouping from the TB mapping, when the file provides one.</summary>
    public string? AccountGroup { get; private set; }

    /// <summary>Provenance of the account master row (manual entry, TB import or GL import).</summary>
    public string AccountOrigin { get; private set; } = AccountOrigin.Manual;

    /// <summary>Optional audit-area extension point (audit-area classification is a later phase).</summary>
    public Guid? AuditAreaId { get; private set; }

    public static Account Create(
        Guid accountId,
        Guid engagementId,
        string accountCode,
        string accountName,
        string accountType,
        int displayOrder,
        string createdAtUtc,
        Guid createdBy,
        string? accountGroup = null,
        string? accountOrigin = null,
        Guid? auditAreaId = null)
    {
        accountCode = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        accountName = (accountName ?? string.Empty).Trim();
        accountType = string.IsNullOrWhiteSpace(accountType)
            ? AccountType.Unclassified
            : accountType.Trim().ToUpperInvariant();
        var origin = string.IsNullOrWhiteSpace(accountOrigin) ? AccountOrigin.Manual : accountOrigin.Trim().ToUpperInvariant();

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

        if (!AccountOrigin.All.Contains(origin))
        {
            throw new ValidationException($"'{origin}' is not a supported account origin.");
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
            NormalizedCode = NormalizeCode(accountCode),
            AccountGroup = Blank(accountGroup),
            AccountOrigin = origin,
            AuditAreaId = auditAreaId,
        };
    }

    /// <summary>
    /// Deterministic normalization used for import duplicate detection and
    /// search: upper case, trimmed, with spaces and the usual separators removed.
    /// Digits, letters and any other character meaning are preserved.
    /// </summary>
    public static string NormalizeCode(string? accountCode)
    {
        var source = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        var builder = new System.Text.StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (character is ' ' or '-' or '.' or '/' or '_' or ',' or '\'' or '"' or '(' or ')')
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Links the account to an audit area of the same engagement (or clears the link).</summary>
    public void AssignAuditArea(Guid? auditAreaId)
    {
        AuditAreaId = auditAreaId;
    }

    public void SetGroup(string? accountGroup)
    {
        AccountGroup = Blank(accountGroup);
    }

    private static string? Blank(string? value, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength].ToString(CultureInfo.InvariantCulture);
    }
}
