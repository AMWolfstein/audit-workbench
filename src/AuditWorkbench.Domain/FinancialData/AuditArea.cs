using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// Optional engagement-scoped audit area (Revenue, Receivables, Inventory, ...).
/// This phase provides the extension point only: accounts may be linked to an
/// area manually, and the TB import never requires a classification. The
/// intelligent classification engine arrives with the planning phase.
/// </summary>
public class AuditArea
{
    private AuditArea()
    {
    }

    public Guid AuditAreaId { get; private set; }

    public Guid EngagementId { get; private set; }

    public string AreaCode { get; private set; } = string.Empty;

    public string AreaName { get; private set; } = string.Empty;

    public int DisplayOrder { get; private set; }

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public int RowVersion { get; private set; } = 1;

    public static AuditArea Create(
        Guid auditAreaId,
        Guid engagementId,
        string areaCode,
        string areaName,
        int displayOrder,
        string createdAtUtc,
        Guid createdBy)
    {
        areaCode = (areaCode ?? string.Empty).Trim().ToUpperInvariant();
        areaName = (areaName ?? string.Empty).Trim();

        if (areaCode.Length == 0)
        {
            throw new ValidationException("Audit area code is required.");
        }

        if (areaCode.Length > 32)
        {
            throw new ValidationException("Audit area code must be 32 characters or fewer.");
        }

        if (areaName.Length == 0)
        {
            throw new ValidationException("Audit area name is required.");
        }

        if (displayOrder < 0)
        {
            throw new ValidationException("Display order must not be negative.");
        }

        return new AuditArea
        {
            AuditAreaId = auditAreaId,
            EngagementId = engagementId,
            AreaCode = areaCode,
            AreaName = areaName,
            DisplayOrder = displayOrder,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = createdBy,
            RowVersion = 1,
        };
    }
}
