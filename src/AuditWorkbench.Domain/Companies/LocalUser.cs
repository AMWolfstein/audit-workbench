namespace AuditWorkbench.Domain.Companies;

/// <summary>
/// MVP local identity. ADR-013: the domain depends on an actor abstraction from
/// the start; the single local account below is clearly marked non-production
/// and is replaced by approved local authentication during hardening.
/// </summary>
public class LocalUser
{
    private LocalUser()
    {
    }

    public static readonly Guid LocalActorId = new("00000000-0000-4000-8000-000000000001");

    public const string LocalActorUsername = "local.auditor";

    public const string LocalActorDisplayName = "Local Auditor (non-production local identity)";

    public Guid UserId { get; private set; }

    public string Username { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string Status { get; private set; } = "ACTIVE";

    public bool IsLocalDemo { get; private set; } = true;

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public static LocalUser CreateLocalActor(string createdAtUtc) => new()
    {
        UserId = LocalActorId,
        Username = LocalActorUsername,
        DisplayName = LocalActorDisplayName,
        Status = "ACTIVE",
        IsLocalDemo = true,
        CreatedAtUtc = createdAtUtc,
    };
}
