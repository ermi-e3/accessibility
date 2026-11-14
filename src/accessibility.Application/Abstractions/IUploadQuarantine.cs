namespace Accessibility.Application.Abstractions;

public interface IUploadQuarantine
{
    Task<QuarantinedUpload> WriteAsync(
        Stream source,
        string originalFileName,
        string contentType,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class QuarantinedUpload : IAsyncDisposable
{
    public QuarantinedUpload(
        string filePath,
        string originalFileName,
        string contentType,
        long byteLength,
        string sha256,
        byte[] header,
        Func<ValueTask> dispose)
    {
        FilePath = filePath;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        ByteLength = byteLength;
        Sha256 = sha256;
        Header = header;
        _dispose = dispose;
    }

    private readonly Func<ValueTask> _dispose;

    public string FilePath { get; }

    public string OriginalFileName { get; }

    public string ContentType { get; }

    public long ByteLength { get; }

    public string Sha256 { get; }

    public byte[] Header { get; }

    public ValueTask DisposeAsync()
    {
        return _dispose();
    }
}
