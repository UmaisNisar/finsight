using System.Security.Claims;

namespace FinSight.Api.Auth;

public static class FinSightClaims
{
    /// <summary>Internal user id. Distinct from Google's subject so the Google id never drives authorization.</summary>
    public const string UserId = "finsight:uid";

    public const string IsDemo = "finsight:demo";

    /// <summary>The server-side session (<see cref="SessionService"/>) this cookie belongs to.</summary>
    public const string SessionId = "finsight:sid";

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        principal.FindFirst(UserId)?.Value is { } value && Guid.TryParse(value, out var id)
            ? id
            : throw new UnauthorizedAccessException("The session has no user id.");

    public static bool IsDemoUser(this ClaimsPrincipal principal) => principal.HasClaim(IsDemo, "true");

    public static Guid? GetSessionId(this ClaimsPrincipal principal) =>
        principal.FindFirst(SessionId)?.Value is { } value && Guid.TryParse(value, out var id) ? id : null;

    public static ClaimsPrincipal Create(Guid userId, Guid sessionId, string displayName, string email, bool isDemo, string scheme) =>
        new(new ClaimsIdentity(
        [
            new Claim(UserId, userId.ToString()),
            new Claim(SessionId, sessionId.ToString()),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Email, email),
            new Claim(IsDemo, isDemo ? "true" : "false"),
        ], scheme));
}
