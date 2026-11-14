using Accessibility.Api.Localization;
using Accessibility.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Accessibility.Api.Authorization;

public sealed class ManagementAuthorizationResultHandler(
    IProblemDetailsService problemDetailsService,
    IStringLocalizer<SharedResource> localizer) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden &&
            authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<ScopeOrRoleRequirement>().Any() == true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var details = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = localizer["InsufficientScopeTitle"],
                Type = "https://httpstatuses.io/403",
                Instance = context.Request.Path
            };
            details.Extensions["code"] = "INSUFFICIENT_SCOPE";
            details.Extensions["traceId"] = context.TraceIdentifier;
            await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = details
            });
            return;
        }

        await _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
