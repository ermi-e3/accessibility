using System.ComponentModel.DataAnnotations;
using Accessibility.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Accessibility.Api.Localization;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthenticationController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IAntiforgery antiforgery,
    IStringLocalizer<SharedResource> localizer) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("antiforgery")]
    public ActionResult<AntiforgeryTokenResponse> GetAntiforgeryToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryTokenResponse(tokens.RequestToken!, tokens.HeaderName!));
    }

    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [HttpPost("login")]
    public async Task<ActionResult<AuthenticatedUserResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            return AuthenticationFailed();
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            request.Password,
            request.RememberMe,
            lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return AuthenticationFailed();
        }

        return Ok(await CreateResponseAsync(user));
    }

    [Authorize]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<AuthenticatedUserResponse>> Me()
    {
        var user = await userManager.GetUserAsync(User);
        return user is null ? Unauthorized() : Ok(await CreateResponseAsync(user));
    }

    private UnauthorizedObjectResult AuthenticationFailed()
    {
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = localizer["AuthenticationFailedTitle"],
            Type = "https://httpstatuses.io/401",
            Instance = HttpContext.Request.Path
        };
        details.Extensions["code"] = "INVALID_CREDENTIALS";
        details.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return Unauthorized(details);
    }

    private async Task<AuthenticatedUserResponse> CreateResponseAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        return new AuthenticatedUserResponse(user.Id, user.Email!, user.DisplayName, roles.ToArray());
    }
}

public sealed record LoginRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MaxLength(256)] string Password,
    bool RememberMe = false);

public sealed record AntiforgeryTokenResponse(string RequestToken, string HeaderName);

public sealed record AuthenticatedUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Roles);
