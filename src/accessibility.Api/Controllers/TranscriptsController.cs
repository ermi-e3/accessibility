using Accessibility.Api.Authorization;
using Accessibility.Api.Contracts.V1;
using Accessibility.Api.Multipart;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Player;
using Accessibility.Application.Features.Processing;
using Accessibility.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
public sealed class TranscriptsController(
    IPlayerPublicationService player,
    IProcessingService processing,
    IStreamingMultipartAssetReader multipartReader) : ControllerBase
{
    private const long MaximumImportBytes = 5 * 1024 * 1024;

    [HttpGet("api/v1/lessons/{lessonId:guid}/transcripts")]
    [Authorize(Policy = AccessLearnPolicies.ResourcesRead)]
    public async Task<ActionResult<IReadOnlyList<TranscriptSummaryModel>>> List(
        Guid lessonId,
        CancellationToken cancellationToken) =>
        Ok(await processing.ListTranscriptsAsync(lessonId, cancellationToken));

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

    [HttpGet("api/v1/transcripts/{transcriptId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.ResourcesRead)]
    public async Task<ActionResult<TranscriptEditorModel>> Get(
        Guid transcriptId,
        CancellationToken cancellationToken)
    {
        var transcript = await processing.GetTranscriptAsync(transcriptId, cancellationToken);
        Response.Headers.ETag = $"\"{transcript.Version}\"";
        return Ok(transcript);
    }

    [HttpPut("api/v1/transcripts/{transcriptId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.ResourcesWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<TranscriptEditorModel>> Update(
        Guid transcriptId,
        UpdateTranscriptRequest request,
        CancellationToken cancellationToken)
    {
        var updated = await processing.UpdateTranscriptAsync(
            transcriptId,
            RequireIfMatch(Request),
            new UpdateTranscriptCommand(request.Segments.Select(segment => new TranscriptSegmentCommand(
                segment.Sequence,
                segment.StartMilliseconds,
                segment.EndMilliseconds,
                segment.Text,
                segment.SpeakerLabel)).ToArray()),
            cancellationToken);
        Response.Headers.ETag = $"\"{updated.Version}\"";
        return Ok(updated);
    }

    [HttpPost("api/v1/transcripts/{transcriptId:guid}/draft-revisions")]
    [Authorize(Policy = AccessLearnPolicies.ResourcesWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<TranscriptEditorModel>> CreateDraftRevision(
        Guid transcriptId,
        CancellationToken cancellationToken)
    {
        var draft = await processing.CreateDraftRevisionAsync(transcriptId, cancellationToken);
        Response.Headers.ETag = $"\"{draft.Version}\"";
        return StatusCode(StatusCodes.Status201Created, draft);
    }

    [HttpPost("api/v1/lessons/{lessonId:guid}/transcripts/import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumImportBytes)]
    [DisableFormValueModelBinding]
    [Authorize(Policy = AccessLearnPolicies.ResourcesWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<TranscriptEditorModel>> Import(
        Guid lessonId,
        [FromQuery] string language,
        [FromQuery] string format,
        CancellationToken cancellationToken)
    {
        await processing.EnsureLessonAccessAsync(lessonId, cancellationToken);
        if (!Enum.TryParse<TranscriptExchangeFormat>(format, ignoreCase: true, out var parsedFormat))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["format"] = ["Format must be WebVtt or Srt."]
            });
        }
        if (parsedFormat == TranscriptExchangeFormat.Text)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["format"] = ["Plain text cannot provide synchronized captions. Import WebVtt or Srt."]
            });
        }
        var expectedExtension = parsedFormat == TranscriptExchangeFormat.WebVtt ? ".vtt" : ".srt";
        await using var upload = await multipartReader.ReadAsync(Request, MaximumImportBytes, cancellationToken);
        if (!string.Equals(Path.GetExtension(upload.OriginalFileName), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentTypeMismatchException($"Imported {parsedFormat} captions must use the {expectedExtension} extension.");
        }
        var validContentType = parsedFormat == TranscriptExchangeFormat.WebVtt
            ? upload.ContentType.Equals("text/vtt", StringComparison.OrdinalIgnoreCase)
            : upload.ContentType.Equals("application/x-subrip", StringComparison.OrdinalIgnoreCase) ||
              upload.ContentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
        if (!validContentType)
        {
            throw new ContentTypeMismatchException("Caption MIME type does not match the selected format.");
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(upload.FilePath, cancellationToken);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF) ||
            bytes.Length >= 4 && (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0 ||
                                  bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["file"] = ["Caption files must be UTF-8, not UTF-16 or UTF-32."]
            });
        }
        string content;
        try
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            content = new System.Text.UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["file"] = ["Caption files must be valid UTF-8."]
            });
        }
        var transcript = await processing.ImportTranscriptAsync(
            lessonId,
            new ImportTranscriptCommand(language, parsedFormat, content),
            cancellationToken);
        Response.Headers.ETag = $"\"{transcript.Version}\"";
        return StatusCode(StatusCodes.Status201Created, transcript);
    }

    [HttpGet("api/v1/transcripts/{transcriptId:guid}/export")]
    [Authorize(Policy = AccessLearnPolicies.ResourcesRead)]
    public async Task<IActionResult> Export(
        Guid transcriptId,
        [FromQuery] string format,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<TranscriptExchangeFormat>(format, ignoreCase: true, out var parsedFormat))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["format"] = ["Format must be WebVtt, Srt, or Text."]
            });
        }
        var export = await processing.ExportTranscriptAsync(transcriptId, parsedFormat, cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(export.Content), export.ContentType, export.FileName);
    }

    private static long RequireIfMatch(HttpRequest request)
    {
        var value = request.Headers.IfMatch.ToString().Trim().Trim('"');
        return long.TryParse(value, out var version) && version > 0
            ? version
            : throw new PreconditionRequiredException();
    }
}
