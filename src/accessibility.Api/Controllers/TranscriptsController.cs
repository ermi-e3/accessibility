using Accessibility.Api.Authorization;
using Accessibility.Api.Contracts.V1;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Player;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
public sealed class TranscriptsController(IPlayerPublicationService player) : ControllerBase
{
    [HttpPost("api/v1/lessons/{lessonId:guid}/transcripts")]
    [Authorize(Policy = AccessLearnPolicies.ResourcesWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<TranscriptModel>> Create(
        Guid lessonId,
        CreateManualTranscriptRequest request,
        CancellationToken cancellationToken)
    {
        var transcript = await player.CreateManualTranscriptAsync(
            lessonId,
            new CreateManualTranscriptCommand(
                request.Language,
                request.Segments.Select(segment => new TranscriptSegmentInput(
                    segment.Sequence,
                    segment.StartMilliseconds,
                    segment.EndMilliseconds,
                    segment.Text,
                    segment.SpeakerLabel)).ToArray()),
            cancellationToken);
        Response.Headers.ETag = $"\"{transcript.Version}\"";
        return StatusCode(StatusCodes.Status201Created, transcript);
    }

    [HttpPost("api/v1/transcripts/{transcriptId:guid}/approve")]
    [Authorize(Policy = AccessLearnPolicies.ResourcesWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<TranscriptModel>> Approve(
        Guid transcriptId,
        CancellationToken cancellationToken)
    {
        var transcript = await player.ApproveTranscriptAsync(
            transcriptId,
            RequireIfMatch(Request),
            cancellationToken);
        Response.Headers.ETag = $"\"{transcript.Version}\"";
        return Ok(transcript);
    }

    private static long RequireIfMatch(HttpRequest request)
    {
        var value = request.Headers.IfMatch.ToString().Trim().Trim('"');
        return long.TryParse(value, out var version) && version > 0
            ? version
            : throw new PreconditionRequiredException();
    }
}
