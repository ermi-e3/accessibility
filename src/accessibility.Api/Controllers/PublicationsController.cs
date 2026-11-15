using Accessibility.Api.Authorization;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Player;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/v1/lessons/{lessonId:guid}/publish")]
public sealed class PublicationsController(IPlayerPublicationService player) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AccessLearnPolicies.LessonsPublish)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<PublicationModel>> Publish(
        Guid lessonId,
        CancellationToken cancellationToken)
    {
        var publication = await player.PublishAsync(
            lessonId,
            RequireIfMatch(Request),
            cancellationToken);
        return CreatedAtAction(
            nameof(PublicPlayerController.Manifest),
            "PublicPlayer",
            new { lessonId },
            publication);
    }

    private static long RequireIfMatch(HttpRequest request)
    {
        var value = request.Headers.IfMatch.ToString().Trim().Trim('"');
        return long.TryParse(value, out var version) && version > 0
            ? version
            : throw new PreconditionRequiredException();
    }
}
