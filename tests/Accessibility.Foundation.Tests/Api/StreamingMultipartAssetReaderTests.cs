using System.Net.Http.Headers;
using Accessibility.Api.Multipart;
using Accessibility.Application.Common;
using Accessibility.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Accessibility.Foundation.Tests.Api;

public sealed class StreamingMultipartAssetReaderTests
{
    [Fact]
    public async Task ReadsStandardMultipartContentWithoutModelBinding()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesslearn-multipart-{Guid.NewGuid():N}");
        var quarantine = new TemporaryUploadQuarantine(Options.Create(new UploadQuarantineOptions
        {
            QuarantinePath = directory
        }));
        var reader = new StreamingMultipartAssetReader(quarantine);
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent("RIFF0000WAVE"u8.ToArray());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        multipart.Add(file, "file", "lesson.wav");
        await using var requestBody = new MemoryStream();
        await multipart.CopyToAsync(requestBody);
        requestBody.Position = 0;
        var context = new DefaultHttpContext();
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.Body = requestBody;

        await using (var upload = await reader.ReadAsync(context.Request, 1_024, CancellationToken.None))
        {
            Assert.Equal("lesson.wav", upload.OriginalFileName);
            Assert.Equal("audio/wav", upload.ContentType);
        }

        Directory.Delete(directory);
    }

    [Fact]
    public async Task RejectsMultipleFileSectionsAndCleansFirstQuarantineFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesslearn-multipart-{Guid.NewGuid():N}");
        var reader = CreateReader(directory);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(FileContent(), "file", "first.wav");
        multipart.Add(FileContent(), "file", "second.wav");
        var context = await CreateContextAsync(multipart);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            reader.ReadAsync(context.Request, 1_024, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(directory));
        Directory.Delete(directory);
    }

    [Fact]
    public async Task RejectsOversizeSectionAndLeavesNoQuarantineFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesslearn-multipart-{Guid.NewGuid():N}");
        var reader = CreateReader(directory);
        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(new byte[65]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        multipart.Add(content, "file", "large.wav");
        var context = await CreateContextAsync(multipart);

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            reader.ReadAsync(context.Request, 64, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(directory));
        Directory.Delete(directory);
    }

    private static StreamingMultipartAssetReader CreateReader(string directory)
    {
        return new StreamingMultipartAssetReader(new TemporaryUploadQuarantine(Options.Create(
            new UploadQuarantineOptions { QuarantinePath = directory })));
    }

    private static ByteArrayContent FileContent()
    {
        var content = new ByteArrayContent("RIFF0000WAVE"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        return content;
    }

    private static async Task<DefaultHttpContext> CreateContextAsync(MultipartFormDataContent multipart)
    {
        var requestBody = new MemoryStream();
        await multipart.CopyToAsync(requestBody);
        requestBody.Position = 0;
        var context = new DefaultHttpContext();
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.Body = requestBody;
        context.Response.RegisterForDispose(requestBody);
        return context;
    }
}
