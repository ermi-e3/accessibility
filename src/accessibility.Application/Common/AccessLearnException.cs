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
