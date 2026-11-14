using Accessibility.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Accessibility.Infrastructure.Authorization;

public sealed record ScopeOrRoleRequirement(string Scope) : IAuthorizationRequirement;

public sealed class ScopeOrRoleHandler : AuthorizationHandler<ScopeOrRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeOrRoleRequirement requirement)
    {
        if (context.User.IsInRole(AccessLearnRoles.Administrator) ||
            context.User.IsInRole(AccessLearnRoles.Educator) ||
            context.User.Claims.Any(claim =>
                claim.Type == AccessLearnClaimTypes.Scope && claim.Value == requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
