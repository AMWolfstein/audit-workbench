using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Identity;

/// <summary>Application identity, independent of any authentication mechanism.</summary>
public class User
{
    private User() { }
    public Guid UserId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string Status { get; private set; } = "ACTIVE";
    public bool IsLocalDemo { get; private set; }
    public string CreatedAtUtc { get; private set; } = string.Empty;
    public string? UpdatedAtUtc { get; private set; }
    public bool IsActive => Status == "ACTIVE";

    public static User Create(Guid id, string username, string displayName, string? email,
        string createdAtUtc, bool isLocalDemo = false)
    {
        username = (username ?? string.Empty).Trim().ToLowerInvariant();
        displayName = (displayName ?? string.Empty).Trim();
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (id == Guid.Empty || username.Length == 0 || displayName.Length == 0)
            throw new ValidationException("User id, username and display name are required.");
        return new User { UserId = id, Username = username, DisplayName = displayName, Email = email,
            CreatedAtUtc = createdAtUtc, UpdatedAtUtc = createdAtUtc, IsLocalDemo = isLocalDemo };
    }
}
