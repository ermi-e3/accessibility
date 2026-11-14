using System.Buffers;
using System.Security.Cryptography;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Storage;

public sealed class TemporaryUploadQuarantine(IOptions<UploadQuarantineOptions> options) : IUploadQuarantine
{
    private const int HeaderCapacity = 64;
    private readonly string _quarantinePath = string.IsNullOrWhiteSpace(options.Value.QuarantinePath)
        ? Path.Combine(Path.GetTempPath(), "AccessLearn", "quarantine")
        : Path.GetFullPath(options.Value.QuarantinePath);

    public string QuarantinePath => _quarantinePath;

    public async Task<QuarantinedUpload> WriteAsync(
        Stream source,
        string originalFileName,
        string contentType,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        Directory.CreateDirectory(_quarantinePath);

        var safeFileName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new UnsupportedFileTypeException("A file name is required.");
        }

        var filePath = Path.Combine(_quarantinePath, $"{Guid.CreateVersion7():N}.upload");
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var header = new byte[HeaderCapacity];
        var headerLength = 0;
        long byteLength = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    byteLength += read;
                    if (byteLength > maximumBytes)
                    {
                        throw new FileTooLargeException(maximumBytes);
                    }

                    if (headerLength < HeaderCapacity)
                    {
                        var copyLength = Math.Min(read, HeaderCapacity - headerLength);
                        buffer.AsSpan(0, copyLength).CopyTo(header.AsSpan(headerLength));
                        headerLength += copyLength;
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            if (byteLength == 0)
            {
                throw new FileContentInvalidException("The uploaded file is empty.");
            }

            var disposed = 0;
            return new QuarantinedUpload(
                filePath,
                safeFileName,
                contentType.Trim().ToLowerInvariant(),
                byteLength,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                header[..headerLength],
                () =>
                {
                    if (Interlocked.Exchange(ref disposed, 1) == 0)
                    {
                        File.Delete(filePath);
                    }

                    return ValueTask.CompletedTask;
                });
        }
        catch
        {
            File.Delete(filePath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
