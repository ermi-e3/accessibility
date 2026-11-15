namespace Accessibility.Application.Common;

public abstract class AccessLearnException : Exception
{
    protected AccessLearnException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class ResourceNotFoundException : AccessLearnException
{
    public ResourceNotFoundException(string resourceName, object resourceId)
        : base("RESOURCE_NOT_FOUND", $"{resourceName} '{resourceId}' was not found.")
    {
    }
}

public sealed class StateConflictException : AccessLearnException
{
    public StateConflictException(string message)
        : base("STATE_CONFLICT", message)
    {
    }
}

public sealed class RequestValidationException : AccessLearnException
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("VALIDATION_ERROR", "One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public sealed class StorageUnavailableException : AccessLearnException
{
    public StorageUnavailableException(string message, Exception? innerException = null)
        : base("STORAGE_UNAVAILABLE", message, innerException)
    {
    }
}

public sealed class FileTooLargeException : AccessLearnException
{
    public FileTooLargeException(long maximumBytes)
        : base("FILE_TOO_LARGE", $"The upload exceeds the maximum of {maximumBytes} bytes.")
    {
    }
}

public sealed class UnsupportedFileTypeException : AccessLearnException
{
    public UnsupportedFileTypeException(string message)
        : base("UNSUPPORTED_FILE_TYPE", message)
    {
    }
}

public sealed class ContentTypeMismatchException : AccessLearnException
{
    public ContentTypeMismatchException(string message)
        : base("CONTENT_TYPE_MISMATCH", message)
    {
    }
}

public sealed class FileContentInvalidException : AccessLearnException
{
    public FileContentInvalidException(string message)
        : base("FILE_CONTENT_INVALID", message)
    {
    }
}

public sealed class ProcessorUnavailableException : AccessLearnException
{
    public ProcessorUnavailableException(string message, Exception? innerException = null)
        : base("PROCESSOR_UNAVAILABLE", message, innerException)
    {
    }
}

public sealed class DuplicateActiveResourceException : AccessLearnException
{
    public DuplicateActiveResourceException(string message)
        : base("DUPLICATE_ACTIVE_RESOURCE", message)
    {
    }
}

public sealed class PreconditionRequiredException : AccessLearnException
{
    public PreconditionRequiredException()
        : base("PRECONDITION_REQUIRED", "If-Match is required for this operation.")
    {
    }
}

public sealed class VersionConflictException : AccessLearnException
{
    public VersionConflictException()
        : base("VERSION_CONFLICT", "The resource changed. Reload it before retrying.")
    {
    }
}
