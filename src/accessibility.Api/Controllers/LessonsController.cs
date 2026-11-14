using Accessibility.Api.Authorization;
using Accessibility.Api.Configuration;
using Accessibility.Api.Contracts.V1;
using Accessibility.Api.Multipart;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Lessons;
using Accessibility.Application.Features.Media;
using Accessibility.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/v1/lessons")]
public sealed class LessonsController(
    ILessonIngestService lessons,
    IStreamingMultipartAssetReader multipartReader) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AccessLearnPolicies.LessonsWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<LessonModel>> Create(
        CreateLessonRequest request,
        CancellationToken cancellationToken)
    {
        var lesson = await lessons.CreateAsync(
            new CreateLessonCommand(request.Title, request.Description, request.SourceLanguage),
            cancellationToken);
        Response.Headers.ETag = ETag(lesson.Version);
        return CreatedAtAction(nameof(Get), new { lessonId = lesson.Id }, lesson);
    }

    [HttpGet]
    [Authorize(Policy = AccessLearnPolicies.LessonsRead)]
    public async Task<ActionResult<LessonPageModel>> List(
        [FromQuery] string? status,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        LessonStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<LessonStatus>(status, ignoreCase: true, out var statusValue))
            {
                throw Validation("status", "Status is invalid.");
            }

            parsedStatus = statusValue;
        }

        return Ok(await lessons.ListAsync(
            new LessonListQuery(parsedStatus, cursor, limit),
            cancellationToken));
    }

    [HttpGet("{lessonId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.LessonsRead)]
    public async Task<ActionResult<LessonDetailModel>> Get(Guid lessonId, CancellationToken cancellationToken)
    {
        var lesson = await lessons.GetAsync(lessonId, cancellationToken);
        Response.Headers.ETag = ETag(lesson.Lesson.Version);
        return Ok(lesson);
    }

    [HttpPost("{lessonId:guid}/assets")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(AssetUploadPolicy.AbsoluteRequestLimit)]
    [DisableFormValueModelBinding]
    [EnableRateLimiting(RateLimitPolicies.MediaUpload)]
    [Authorize(Policy = AccessLearnPolicies.ResourcesWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<AssetModel>> UploadAsset(
        Guid lessonId,
        [FromQuery] string role,
        [FromQuery] string? language,
        [FromQuery] long syncOffsetMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseRole(role, out var parsedRole))
        {
            throw Validation("role", "Role is invalid or cannot be uploaded directly.");
        }

        var rule = AssetUploadPolicy.For(parsedRole);
        await lessons.EnsureCanManageAsync(lessonId, cancellationToken);
        await using var upload = await multipartReader.ReadAsync(Request, rule.MaximumBytes, cancellationToken);
        var asset = await lessons.IngestAssetAsync(
            lessonId,
            new AssetIngestCommand(parsedRole, language, syncOffsetMs),
            upload,
            cancellationToken);
        return CreatedAtAction(nameof(Get), new { lessonId }, asset);
    }

    private static bool TryParseRole(string value, out MediaAssetRole role)
    {
        if (Enum.TryParse(value, ignoreCase: true, out role) &&
            role is MediaAssetRole.PrimaryVideo or
                MediaAssetRole.PrimaryAudio or
                MediaAssetRole.SignLanguage or
                MediaAssetRole.AudioDescription)
        {
            return true;
        }

        role = default;
        return false;
    }

    private static string ETag(long version) => $"\"{version}\"";

    private static RequestValidationException Validation(string field, string message)
    {
        return new RequestValidationException(new Dictionary<string, string[]> { [field] = [message] });
    }
}
