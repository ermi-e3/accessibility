using Accessibility.Application.Features.Player;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/v1/public/documents")]
[AllowAnonymous]
[EnableCors("PublicRead")]
public sealed class PublicDocumentsController(IPlayerPublicationService publications) : ControllerBase
{
    [HttpGet("{outputId:guid}")]
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> Get(Guid outputId, CancellationToken cancellationToken)
    {
        var document = await publications.GetPublicDocumentAsync(outputId, cancellationToken);
        HttpContext.Response.OnCompleted(() => document.DisposeAsync().AsTask());
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'self'";
        Response.Headers["X-AccessLearn-Document-Sha256"] = document.Sha256;
        if (!string.IsNullOrWhiteSpace(document.Resource.ETag))
        {
            Response.Headers.ETag = $"\"{document.Resource.ETag.Trim('\"')}\"";
        }
        Response.ContentLength = document.Resource.ByteLength;
        return File(
            document.Resource.Content,
            document.Resource.ContentType,
            enableRangeProcessing: true);
    }
}
