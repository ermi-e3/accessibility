using System.Security.Cryptography;
using Accessibility.Application.Common;
using Accessibility.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class TemporaryUploadQuarantineTests
{
    [Fact]
    public async Task StreamsHashesAndDeletesQuarantinedUpload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesslearn-quarantine-test-{Guid.NewGuid():N}");
        var quarantine = new TemporaryUploadQuarantine(Options.Create(new UploadQuarantineOptions
        {
            QuarantinePath = directory
        }));
        var payload = "RIFF0000WAVE-accesslearn"u8.ToArray();

        await using (var upload = await quarantine.WriteAsync(
            new NonSeekableReadStream(payload),
            "lesson.wav",
            "audio/wav",
            1_024,
            CancellationToken.None))
        {
            Assert.Equal(payload.Length, upload.ByteLength);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), upload.Sha256);
            Assert.True(File.Exists(upload.FilePath));
            Assert.Equal(payload, await File.ReadAllBytesAsync(upload.FilePath));
        }

        Assert.Empty(Directory.EnumerateFiles(directory));
        Directory.Delete(directory);
    }

    [Fact]
    public async Task OversizeUploadIsRejectedAndCleaned()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesslearn-quarantine-test-{Guid.NewGuid():N}");
        var quarantine = new TemporaryUploadQuarantine(Options.Create(new UploadQuarantineOptions
        {
            QuarantinePath = directory
        }));

        await Assert.ThrowsAsync<FileTooLargeException>(() => quarantine.WriteAsync(
            new MemoryStream(new byte[65]),
            "lesson.wav",
            "audio/wav",
            64,
            CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(directory));
        Directory.Delete(directory);
    }

    [Fact]
    public async Task StartupCleanupRemovesOnlyExpiredQuarantineFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesslearn-quarantine-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var stale = Path.Combine(directory, "stale.upload");
        var recent = Path.Combine(directory, "recent.upload");
        await File.WriteAllTextAsync(stale, "stale");
        await File.WriteAllTextAsync(recent, "recent");
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(stale, now.AddHours(-3).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, now.AddMinutes(-5).UtcDateTime);
        var options = Options.Create(new UploadQuarantineOptions
        {
            QuarantinePath = directory,
            MaximumAgeMinutes = 60
        });
        var quarantine = new TemporaryUploadQuarantine(options);
        var cleanup = new UploadQuarantineCleanupService(
            quarantine,
            options,
            new FixedTimeProvider(now),
            NullLogger<UploadQuarantineCleanupService>.Instance);

        await cleanup.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
        Directory.Delete(directory, recursive: true);
    }

    private sealed class NonSeekableReadStream(byte[] value) : Stream
    {
        private readonly MemoryStream _inner = new(value, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
