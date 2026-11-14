using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Accessibility.Api.Localization;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Accessibility.Api.Identity;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIntegrationApiKeyService apiKeys,
    IProblemDetailsService problemDetailsService,
    IStringLocalizer<SharedResource> localizer)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private string _challengeCode = "INVALID_API_KEY";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return AuthenticateResult.NoResult();
        }

        var client = await apiKeys.AuthenticateAsync(header.Parameter, Context.RequestAborted);
        if (client is null)
        {
            return AuthenticateResult.Fail("INVALID_API_KEY");
        }

        var origin = Request.Headers.Origin.ToString().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(origin) &&
            !client.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            _challengeCode = "ORIGIN_NOT_ALLOWED";
            return AuthenticateResult.Fail(_challengeCode);
        }

        await apiKeys.RecordUseAsync(client.ClientId, Context.RequestAborted);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.OwnerUserId.ToString()),
            new(ClaimTypes.Name, client.Name),
            new(AccessLearnClaimTypes.IntegrationClientId, client.ClientId.ToString())
        };
        claims.AddRange(client.Scopes.Select(scope => new Claim(AccessLearnClaimTypes.Scope, scope)));

        var identity = new ClaimsIdentity(claims, AccessLearnAuthenticationSchemes.ApiKey);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            AccessLearnAuthenticationSchemes.ApiKey));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = localizer["AuthenticationFailedTitle"],
            Type = "https://httpstatuses.io/401",
            Instance = Request.Path
        };
        details.Extensions["code"] = Request.Headers.ContainsKey("Authorization")
            ? _challengeCode
            : "AUTHENTICATION_REQUIRED";
        details.Extensions["traceId"] = Context.TraceIdentifier;
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = details
        });
    }
}
