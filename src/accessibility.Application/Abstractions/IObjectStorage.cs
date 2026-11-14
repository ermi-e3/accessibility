namespace Accessibility.Application.Abstractions;

public interface IObjectStorage
{
    Task<ObjectStorageWriteResult> PutAsync(
        string objectKey,
        Stream content,
        string contentType,
        long byteLength,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task<ObjectStorageReadResult> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);

    Task<Uri> CreateReadUriAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

public sealed record ObjectStorageWriteResult(string ObjectKey, string? ETag, long ByteLength);

public sealed class ObjectStorageReadResult : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public ObjectStorageReadResult(Stream content, string contentType, long byteLength, string? eTag, IDisposable? owner = null)
    {
        Content = content;
        ContentType = contentType;
        ByteLength = byteLength;
        ETag = eTag;
        _owner = owner;
    }

    public Stream Content { get; }

    public string ContentType { get; }

    public long ByteLength { get; }

    public string? ETag { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        _owner?.Dispose();
    }
}
