using Accessibility.Api.Authorization;
using Accessibility.Api.Contracts.V1;
using Accessibility.Application.Authorization;
using Accessibility.Application.Features.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
public sealed class ProcessingController(IProcessingService processing) : ControllerBase
{
    [HttpPost("api/v1/lessons/{lessonId:guid}/processing-jobs")]
    [Authorize(Policy = AccessLearnPolicies.ProcessingWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<ProcessingJobModel>> Start(
        Guid lessonId,
        StartProcessingRequest request,
        CancellationToken cancellationToken)
    {
        var job = await processing.StartAsync(
            lessonId,
            new StartProcessingCommand(request.SourceAssetId, request.TargetLanguages),
            Request.Headers["Idempotency-Key"].ToString(),
            cancellationToken);
        return AcceptedAtAction(nameof(Get), new { jobId = job.Id }, job);
    }

    [HttpGet("api/v1/processing-jobs/{jobId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.ProcessingRead)]
    public async Task<ActionResult<ProcessingJobModel>> Get(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await processing.GetAsync(jobId, cancellationToken);
        Response.Headers.ETag = $"\"{job.Version}\"";
        return Ok(job);
    }

    [HttpPost("api/v1/processing-jobs/{jobId:guid}/retry")]
    [Authorize(Policy = AccessLearnPolicies.ProcessingWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<ProcessingJobModel>> Retry(Guid jobId, CancellationToken cancellationToken) =>
        Accepted(await processing.RetryAsync(jobId, cancellationToken));

    [HttpPost("api/v1/processing-jobs/{jobId:guid}/cancel")]
    [Authorize(Policy = AccessLearnPolicies.ProcessingWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<ProcessingJobModel>> Cancel(Guid jobId, CancellationToken cancellationToken) =>
        Ok(await processing.CancelAsync(jobId, cancellationToken));
}
