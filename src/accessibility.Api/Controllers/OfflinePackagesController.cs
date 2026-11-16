using Accessibility.Api.Authorization;
using Accessibility.Api.Configuration;
using Accessibility.Application.Authorization;
using Accessibility.Application.Features.Offline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
public sealed class OfflinePackagesController(IOfflinePackageService offline) : ControllerBase
{
    [HttpPost("api/v1/lessons/{lessonId:guid}/offline-packages")]
    [Authorize(Policy = AccessLearnPolicies.OfflineWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<OfflinePackageModel>> Start(
        Guid lessonId,
        CancellationToken cancellationToken)
    {
        var package = await offline.StartAsync(lessonId, cancellationToken);
        return AcceptedAtAction(nameof(Get), new { packageId = package.Id }, package);
    }

    [HttpGet("api/v1/offline-packages/{packageId:guid}")]
    [Authorize(Policy = AccessLearnPolicies.OfflineRead)]
    public async Task<ActionResult<OfflinePackageModel>> Get(
        Guid packageId,
        CancellationToken cancellationToken)
    {
        var package = await offline.GetAsync(packageId, cancellationToken);
        Response.Headers.ETag = $"\"{package.Version}\"";
        return Ok(package);
    }

    [HttpPost("api/v1/offline-packages/{packageId:guid}/retry")]
    [Authorize(Policy = AccessLearnPolicies.OfflineWrite)]
    [ServiceFilter<ManagementAntiforgeryFilter>]
    public async Task<ActionResult<OfflinePackageModel>> Retry(
        Guid packageId,
        CancellationToken cancellationToken) =>
        Accepted(await offline.RetryAsync(packageId, cancellationToken));

    [HttpGet("api/v1/public/lessons/{lessonId:guid}/offline-manifest")]
    [AllowAnonymous]
    [EnableCors(CorsSettings.PublicReadPolicy)]
    public async Task<ActionResult<PublicOfflineManifestV1>> PublicManifest(
        Guid lessonId,
        CancellationToken cancellationToken)
    {
        var manifest = await offline.GetCurrentPublicManifestAsync(lessonId, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ETag = $"\"{manifest.PackageId:N}-{manifest.PublicationRevision}\"";
        return Ok(manifest);
    }

    [HttpGet("api/v1/public/lessons/{lessonId:guid}/offline-package.zip")]
    [AllowAnonymous]
    [EnableCors(CorsSettings.PublicReadPolicy)]
    public async Task<IActionResult> PublicZip(Guid lessonId, CancellationToken cancellationToken)
    {
        var manifest = await offline.GetCurrentPublicManifestAsync(lessonId, cancellationToken);
        return Redirect(manifest.ZipUrl);
    }

    [HttpGet("api/v1/public/offline-packages/{packageId:guid}/manifest")]
    [AllowAnonymous]
    [EnableCors(CorsSettings.PublicReadPolicy)]
    public async Task<ActionResult<PublicOfflineManifestV1>> PackageManifest(
        Guid packageId,
        CancellationToken cancellationToken)
    {
        var manifest = await offline.GetPublicManifestAsync(packageId, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ETag = $"\"{manifest.PackageId:N}-{manifest.PublicationRevision}\"";
        return Ok(manifest);
    }

    [HttpGet("api/v1/public/offline-packages/{packageId:guid}/package.zip")]
    [AllowAnonymous]
    [EnableCors(CorsSettings.PublicReadPolicy)]
    public async Task<IActionResult> PackageZip(Guid packageId, CancellationToken cancellationToken)
    {
        var manifest = await offline.GetPublicManifestAsync(packageId, cancellationToken);
        return Redirect(manifest.ZipUrl);
    }
}
