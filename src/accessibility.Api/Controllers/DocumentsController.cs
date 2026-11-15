using Accessibility.Api.Authorization;
using Accessibility.Api.Multipart;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/v1/lessons/{lessonId:guid}/documents")]
public sealed class DocumentsController(
    IProcessingService processing,
    IStreamingMultipartAssetReader multipartReader) : ControllerBase
{
    private const long MaximumDocumentBytes = 25 * 1024 * 1024;

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumDocumentBytes)]
    [DisableFormValueModelBinding]
    [Authorize(Policy = AccessLearnPolicies.ResourcesWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<DocumentSourceModel>> Upload(
        Guid lessonId,
        [FromQuery] string title,
        [FromQuery] string language,
        CancellationToken cancellationToken)
    {
        await processing.EnsureLessonAccessAsync(lessonId, cancellationToken);
        await using var upload = await multipartReader.ReadAsync(Request, MaximumDocumentBytes, cancellationToken);
        var document = await processing.UploadDocumentAsync(
            lessonId,
            new UploadDocumentCommand(title, language),
            upload,
            cancellationToken);
        Response.Headers.ETag = $"\"{document.Version}\"";
        return StatusCode(StatusCodes.Status201Created, document);
    }
}
