using System.Security.Claims;

namespace CitusManager.Endpoints;

internal static class EndpointUser
{
    public static Guid Id(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated user identifier is invalid.");
    }
}
