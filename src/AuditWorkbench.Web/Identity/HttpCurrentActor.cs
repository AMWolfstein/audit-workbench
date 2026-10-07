using System.Security.Claims;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Web.Identity;

/// <summary>
/// Resolves the actor exclusively from the authenticated server principal. No
/// route, form, query-string or command value participates in identity resolution.
/// The authentication handler remains a deployment concern (OIDC, Windows, etc.).
/// </summary>
public sealed class HttpCurrentActor : ICurrentActor
{
    private readonly IHttpContextAccessor _http;

    public HttpCurrentActor(IHttpContextAccessor http) => _http = http;

    private ClaimsPrincipal Principal => _http.HttpContext?.User ?? new ClaimsPrincipal();

    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true && UserId != Guid.Empty;

    public Guid UserId => Guid.TryParse(
        Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? Principal.FindFirstValue("sub"), out var id)
        ? id
        : Guid.Empty;

    public string Username => Principal.FindFirstValue(ClaimTypes.Name)
        ?? Principal.Identity?.Name
        ?? string.Empty;

    public string DisplayName => Principal.FindFirstValue("name")
        ?? Principal.FindFirstValue(ClaimTypes.GivenName)
        ?? Username;

    public string AuthenticationMethod => Principal.Identity?.AuthenticationType ?? "Unauthenticated";

    public bool IsLocalDemoIdentity => false;
}
