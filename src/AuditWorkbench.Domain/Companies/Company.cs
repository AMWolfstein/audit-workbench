using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Companies;

/// <summary>
/// Master data: "who is the client?". A company is never deleted and never
/// carries a mutable "current financial year" (ADR-001).
/// </summary>
public class Company
{
    private Company()
    {
    }

    public Guid CompanyId { get; private set; }

    public string LegalName { get; private set; } = string.Empty;

    /// <summary>Short, non-sensitive internal office reference (unique).</summary>
    public string ShortName { get; private set; } = string.Empty;

    public string Industry { get; private set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    /// <summary>Optional tax or registration reference.</summary>
    public string? TaxReference { get; private set; }

    public string Status { get; private set; } = CompanyStatus.Active;

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public string? ArchivedAtUtc { get; private set; }

    public int RowVersion { get; private set; } = 1;

    public static Company Create(
        Guid companyId,
        string legalName,
        string shortName,
        string industry,
        string countryCode,
        string? taxReference,
        string createdAtUtc,
        Guid createdBy)
    {
        legalName = (legalName ?? string.Empty).Trim();
        shortName = (shortName ?? string.Empty).Trim();
        industry = (industry ?? string.Empty).Trim();
        countryCode = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        taxReference = string.IsNullOrWhiteSpace(taxReference) ? null : taxReference.Trim();

        if (legalName.Length == 0)
        {
            throw new ValidationException("Legal name is required.");
        }

        if (shortName.Length == 0)
        {
            throw new ValidationException("Short name is required.");
        }

        if (industry.Length == 0)
        {
            throw new ValidationException("Industry is required.");
        }

        if (countryCode.Length != 2 || !countryCode.All(char.IsLetter))
        {
            throw new ValidationException("Country must be a two-letter ISO 3166-1 alpha-2 code.");
        }

        return new Company
        {
            CompanyId = companyId,
            LegalName = legalName,
            ShortName = shortName,
            Industry = industry,
            CountryCode = countryCode,
            TaxReference = taxReference,
            Status = CompanyStatus.Active,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = createdBy,
            RowVersion = 1,
        };
    }

    public bool IsActive => Status == CompanyStatus.Active;
}
