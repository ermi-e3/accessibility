using Accessibility.Api.Configuration;
using Accessibility.Application.Features.Player;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Accessibility.Infrastructure.Player;
using Microsoft.Extensions.Options;

namespace Accessibility.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableCors(CorsSettings.PublicReadPolicy)]
[Route("api/v1/public/lessons/{lessonId:guid}")]
public sealed class PublicPlayerController(
    IPlayerPublicationService player,
    IOptions<PlayerDeliveryOptions> options) : ControllerBase
{
    [HttpGet("manifest", Name = "PublicPlayerManifest")]
    public async Task<ActionResult<PlayerManifestV1>> Manifest(
        Guid lessonId,
        CancellationToken cancellationToken)
    {
        var apiBase = new Uri(options.Value.PublicApiBaseUrl.TrimEnd('/') + "/");
        var manifest = await player.GetManifestAsync(lessonId, apiBase, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ETag = $"\"{manifest.PublicationId:N}-{manifest.Revision}\"";
        return Ok(manifest);
    }

    [HttpGet("publications/{publicationId:guid}/transcripts/{language}")]
    public async Task<ActionResult<PublicTranscriptV1>> Transcript(
        Guid lessonId,
        Guid publicationId,
        string language,
        CancellationToken cancellationToken)
    {
        var transcript = await player.GetPublicTranscriptAsync(
            lessonId,
            publicationId,
            language,
            cancellationToken);
        Response.Headers.CacheControl = "public,max-age=300";
        Response.Headers.ETag = $"\"{transcript.PublicationId:N}-{transcript.Language}-{transcript.Revision}\"";
        return Ok(transcript);
    }
}
