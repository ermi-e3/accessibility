using Accessibility.Application.Authorization;
using Accessibility.Api.Localization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Accessibility.Api.Authorization;

public sealed class ManagementAntiforgeryFilter(
    IAntiforgery antiforgery,
    IStringLocalizer<SharedResource> localizer,
    IOptions<AntiforgeryOptions> options) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.AuthenticationType == AccessLearnAuthenticationSchemes.ApiKey)
        {
            return;
        }

        var headerName = options.Value.HeaderName;
        if (string.IsNullOrWhiteSpace(headerName) || !context.HttpContext.Request.Headers.ContainsKey(headerName))
        {
            context.Result = InvalidAntiforgery(context.HttpContext);
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = InvalidAntiforgery(context.HttpContext);
        }
    }

    private BadRequestObjectResult InvalidAntiforgery(HttpContext context)
    {
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = localizer["ValidationErrorTitle"],
            Type = "https://httpstatuses.io/400",
            Instance = context.Request.Path
        };
        details.Extensions["code"] = "VALIDATION_ERROR";
        details.Extensions["traceId"] = context.TraceIdentifier;
        return new BadRequestObjectResult(details);
    }
}
