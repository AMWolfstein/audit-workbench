using AuditWorkbench.Application.Auditing;
using Microsoft.AspNetCore.Mvc;

namespace AuditWorkbench.Web.Pages;

public class AuditTrailModel : WorkbenchPageModel
{
    private readonly AuditTrailQuery _auditTrail;

    public AuditTrailModel(AuditTrailQuery auditTrail)
    {
        _auditTrail = auditTrail;
    }

    public IReadOnlyList<AuditEventRow> Events { get; private set; } = Array.Empty<AuditEventRow>();

    public bool ChainValid { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid? EngagementId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? CompanyId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? EventType { get; set; }

    public IReadOnlyList<string> EventTypes => AuditTrailQuery.KnownEventTypes;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Events = await _auditTrail.ListAsync(CompanyId, EngagementId, EventType, 300, cancellationToken);
        ChainValid = await _auditTrail.VerifyChainAsync(cancellationToken);
    }
}
