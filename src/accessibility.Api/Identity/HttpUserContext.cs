using System.Security.Claims;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;

namespace Accessibility.Api.Identity;

public sealed class HttpUserContext(IHttpContextAccessor httpContextAccessor) : IUserContext
{
    private ClaimsPrincipal? User => httpContextAccessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var value = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }

    public Guid? IntegrationClientId
    {
        get
        {
            var value = User?.FindFirstValue(AccessLearnClaimTypes.IntegrationClientId);
            return Guid.TryParse(value, out var clientId) ? clientId : null;
        }
    }

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public bool IsIntegrationClient => IntegrationClientId.HasValue;

    public bool IsInRole(string role)
    {
        return User?.IsInRole(role) == true;
    }

    public bool HasScope(string scope)
    {
        return User?.Claims.Any(claim => claim.Type == AccessLearnClaimTypes.Scope && claim.Value == scope) == true;
    }
}
