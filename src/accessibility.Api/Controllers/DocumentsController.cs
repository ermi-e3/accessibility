using System.Text;
using Accessibility.Api.Authorization;
using Accessibility.Api.Multipart;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Documents;
using Accessibility.Application.Features.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class DocumentsController(
    IDocumentService documents,
    IProcessingService processing,
    IStreamingMultipartAssetReader multipartReader) : ControllerBase
{
    private const long MaximumDocumentBytes = 25 * 1024 * 1024;

    [HttpPost("lessons/{lessonId:guid}/documents")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumDocumentBytes + 256 * 1024)]
    [DisableFormValueModelBinding]
    [Authorize(Policy = AccessLearnPolicies.DocumentsWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<DocumentUploadResult>> Upload(
        Guid lessonId,
        [FromQuery] string? title,
        [FromQuery] string? language,
        CancellationToken cancellationToken)
    {
        await using var upload = await multipartReader.ReadWithFieldsAsync(
            Request,
            MaximumDocumentBytes,
            new HashSet<string>(StringComparer.Ordinal) { "title", "language" },
            cancellationToken);
        var resolvedTitle = ResolveField("title", title, upload.Fields);
        var resolvedLanguage = ResolveField("language", language, upload.Fields);
        var result = await documents.UploadAsync(
            lessonId,
            new Accessibility.Application.Features.Documents.UploadDocumentCommand(resolvedTitle, resolvedLanguage),
            upload.File,
            cancellationToken);
        Response.Headers.Location = Url.ActionLink(nameof(Get), values: new { documentId = result.Document.Id });
        Response.Headers.ETag = ETag(result.Document.Version);
        return StatusCode(StatusCodes.Status202Accepted, result);
    }

    [HttpGet("documents/{documentId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public async Task<ActionResult<DocumentDetailModel>> Get(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(documentId, cancellationToken);
        Response.Headers.ETag = ETag(document.Version);
        return Ok(document);
    }

    [HttpPatch("documents/{documentId:guid}/images/{imageId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<DocumentDetailModel>> ReviewImage(
        Guid documentId,
        Guid imageId,
        [FromBody] ReviewDocumentImageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await documents.ReviewImageAsync(
            documentId,
            imageId,
            RequireIfMatch(Request),
            new ReviewDocumentImageCommand(request.AltText, request.IsDecorative),
            cancellationToken);
        Response.Headers.ETag = ETag(document.Version);
        return Ok(document);
    }

    [HttpGet("documents/{documentId:guid}/images/{imageId:guid}/content")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public async Task<IActionResult> ImageContent(
        Guid documentId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var download = await documents.OpenImageAsync(documentId, imageId, cancellationToken);
        HttpContext.Response.OnCompleted(() => download.DisposeAsync().AsTask());
        Response.ContentLength = download.ByteLength;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(download.Content, download.ContentType, enableRangeProcessing: true);
    }

    [HttpGet("documents/{documentId:guid}/normalized-model")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public async Task<IActionResult> NormalizedModel(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var download = await documents.OpenNormalizedModelAsync(documentId, cancellationToken);
        HttpContext.Response.OnCompleted(() => download.DisposeAsync().AsTask());
        Response.ContentLength = download.ByteLength;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(download.Content, download.ContentType, download.FileName, enableRangeProcessing: true);
    }

    [HttpGet("documents/{documentId:guid}/preview")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public async Task<ContentResult> Preview(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var preview = await documents.GetPreviewAsync(documentId, cancellationToken);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'; object-src 'none'";
        return Content(preview.Content, preview.ContentType, Encoding.UTF8);
    }

    [HttpPost("documents/{documentId:guid}/outputs")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<ProcessingJobModel>> GenerateOutputs(
        Guid documentId,
        [FromBody] GenerateDocumentOutputsRequest request,
        CancellationToken cancellationToken)
    {
        var job = await documents.GenerateOutputsAsync(
            documentId,
            RequireIfMatch(Request),
            new GenerateDocumentOutputsCommand(request.Formats),
            Request.Headers["Idempotency-Key"].ToString(),
            cancellationToken);
        Response.Headers.Location = Url.ActionLink(
            nameof(ProcessingController.Get),
            "Processing",
            new { jobId = job.Id });
        return Accepted(job);
    }

    [HttpGet("documents/{documentId:guid}/outputs")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public async Task<ActionResult<IReadOnlyList<DocumentOutputModel>>> Outputs(
        Guid documentId,
        CancellationToken cancellationToken) =>
        Ok(await documents.ListOutputsAsync(documentId, cancellationToken));

    [HttpGet("documents/{documentId:guid}/jobs/{jobId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public async Task<ActionResult<ProcessingJobModel>> Job(
        Guid documentId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        _ = await documents.GetAsync(documentId, cancellationToken);
        var job = await processing.GetAsync(jobId, cancellationToken);
        return job.DocumentId == documentId
            ? Ok(job)
            : throw new ResourceNotFoundException("Document job", jobId);
    }

    [HttpPost("documents/{documentId:guid}/jobs/{jobId:guid}/retry")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<ProcessingJobModel>> RetryJob(
        Guid documentId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        _ = await documents.GetAsync(documentId, cancellationToken);
        var current = await processing.GetAsync(jobId, cancellationToken);
        if (current.DocumentId != documentId)
        {
            throw new ResourceNotFoundException("Document job", jobId);
        }
        return Accepted(await processing.RetryAsync(jobId, cancellationToken));
    }

    [HttpPost("documents/{documentId:guid}/jobs/{jobId:guid}/cancel")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<ProcessingJobModel>> CancelJob(
        Guid documentId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        _ = await documents.GetAsync(documentId, cancellationToken);
        var current = await processing.GetAsync(jobId, cancellationToken);
        if (current.DocumentId != documentId)
        {
            throw new ResourceNotFoundException("Document job", jobId);
        }
        return Ok(await processing.CancelAsync(jobId, cancellationToken));
    }

    [HttpGet("documents/{documentId:guid}/outputs/{outputId:guid}/download")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public Task<IActionResult> Download(
        Guid documentId,
        Guid outputId,
        CancellationToken cancellationToken) =>
        DownloadFile(documentId, outputId, validationReport: false, cancellationToken);

    [HttpGet("documents/{documentId:guid}/outputs/{outputId:guid}/validation")]
    [Authorize(Policy = AccessLearnPolicies.DocumentsRead)]
    public Task<IActionResult> ValidationReport(
        Guid documentId,
        Guid outputId,
        CancellationToken cancellationToken) =>
        DownloadFile(documentId, outputId, validationReport: true, cancellationToken);

    private async Task<IActionResult> DownloadFile(
        Guid documentId,
        Guid outputId,
        bool validationReport,
        CancellationToken cancellationToken)
    {
        var download = await documents.OpenOutputAsync(documentId, outputId, validationReport, cancellationToken);
        HttpContext.Response.OnCompleted(() => download.DisposeAsync().AsTask());
        if (!string.IsNullOrWhiteSpace(download.ETag))
        {
            Response.Headers.ETag = $"\"{download.ETag.Trim('\"')}\"";
        }
        Response.ContentLength = download.ByteLength;
        return File(download.Content, download.ContentType, download.FileName, enableRangeProcessing: true);
    }

    private static string ETag(long version) => $"\"{version}\"";

    private static string ResolveField(
        string name,
        string? queryValue,
        IReadOnlyDictionary<string, string> fields)
    {
        fields.TryGetValue(name, out var formValue);
        if (!string.IsNullOrWhiteSpace(queryValue) && !string.IsNullOrWhiteSpace(formValue) &&
            !string.Equals(queryValue.Trim(), formValue.Trim(), StringComparison.Ordinal))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                [name] = [$"The query and multipart {name} values do not match."]
            });
        }
        return !string.IsNullOrWhiteSpace(queryValue) ? queryValue : formValue ?? string.Empty;
    }

    private static long RequireIfMatch(HttpRequest request)
    {
        var value = request.Headers.IfMatch.ToString().Trim().Trim('"');
        return long.TryParse(value, out var version) && version > 0
            ? version
            : throw new PreconditionRequiredException();
    }
}

public sealed record ReviewDocumentImageRequest(string? AltText, bool IsDecorative);

public sealed record GenerateDocumentOutputsRequest(IReadOnlyList<string> Formats);
