using Accessibility.Api.Localization;
using Accessibility.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Accessibility.Api.Errors;

public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IStringLocalizer<SharedResource> localizer,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        var (status, titleKey, code) = exception switch
        {
            ResourceNotFoundException =>
                (StatusCodes.Status404NotFound, "NotFoundErrorTitle", "RESOURCE_NOT_FOUND"),
            RequestValidationException =>
                (StatusCodes.Status400BadRequest, "ValidationErrorTitle", "VALIDATION_ERROR"),
            StateConflictException =>
                (StatusCodes.Status409Conflict, "ConflictErrorTitle", "STATE_CONFLICT"),
            StorageUnavailableException =>
                (StatusCodes.Status503ServiceUnavailable, "StorageUnavailableTitle", "STORAGE_UNAVAILABLE"),
            ProcessorUnavailableException processorUnavailable =>
                (StatusCodes.Status503ServiceUnavailable, "ProcessorUnavailableTitle", processorUnavailable.Code),
            FileTooLargeException fileTooLarge =>
                (StatusCodes.Status413PayloadTooLarge, "FileTooLargeTitle", fileTooLarge.Code),
            UnsupportedFileTypeException unsupported =>
                (StatusCodes.Status415UnsupportedMediaType, "UnsupportedFileTitle", unsupported.Code),
            ContentTypeMismatchException mismatch =>
                (StatusCodes.Status415UnsupportedMediaType, "UnsupportedFileTitle", mismatch.Code),
            FileContentInvalidException invalidFile =>
                (StatusCodes.Status422UnprocessableEntity, "InvalidFileTitle", invalidFile.Code),
            DuplicateActiveResourceException duplicate =>
                (StatusCodes.Status409Conflict, "ConflictErrorTitle", duplicate.Code),
            UnauthorizedAccessException =>
                (StatusCodes.Status403Forbidden, "ForbiddenErrorTitle", "FORBIDDEN"),
            _ =>
                (StatusCodes.Status500InternalServerError, "UnexpectedErrorTitle", "UNEXPECTED_ERROR")
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for trace {TraceId}", httpContext.TraceIdentifier);
        }

        var details = new ProblemDetails
        {
            Status = status,
            Title = localizer[titleKey],
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path,
            Detail = null
        };
        details.Extensions["code"] = code;
        details.Extensions["traceId"] = httpContext.TraceIdentifier;

        if (exception is RequestValidationException validationException)
        {
            details.Extensions["errors"] = validationException.Errors.ToDictionary(
                error => error.Key,
                error => error.Value.Select(_ => localizer["InvalidFieldValue"].Value).ToArray());
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception
        });
    }
}
