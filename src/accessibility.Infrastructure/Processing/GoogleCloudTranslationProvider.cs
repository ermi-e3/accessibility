using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using System.Text;
using Google.Cloud.Translate.V3;
using Google.Api.Gax.ResourceNames;
using Microsoft.Extensions.Options;
using Grpc.Core;

namespace Accessibility.Infrastructure.Processing;

public sealed class GoogleCloudTranslationProvider(
    IOptions<GoogleTranslationOptions> options) : ITranslationProvider
{
    private readonly GoogleTranslationOptions _options = options.Value;
    private readonly Lazy<TranslationServiceClient> _client = new(() => TranslationServiceClient.Create());

    public string Name => "google-cloud-translation";

    public string Version => _options.Version;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new ProcessorUnavailableException("Google Translation is disabled.");
        }

        var translated = new List<string>(segments.Count);
        foreach (var batch in CreateBatches(segments, 100, 25_000))
        {
            var request = new TranslateTextRequest
            {
                ParentAsLocationName = LocationName.FromProjectLocation(_options.ProjectId, _options.Location),
                SourceLanguageCode = sourceLanguage,
                TargetLanguageCode = targetLanguage,
                MimeType = "text/plain"
            };
            request.Contents.AddRange(batch);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            TranslateTextResponse response;
            try
            {
                response = await _client.Value.TranslateTextAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                throw new ProcessorUnavailableException("Google Translation timed out.", exception);
            }
            catch (RpcException exception) when (
                exception.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Google Translation was cancelled.", exception, cancellationToken);
            }
            catch (RpcException exception) when (
                exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or
                    StatusCode.ResourceExhausted or StatusCode.Cancelled)
            {
                throw new ProcessorUnavailableException("Google Translation is temporarily unavailable.", exception);
            }
            catch (RpcException exception)
            {
                throw new FileContentInvalidException($"Google Translation rejected the request: {exception.StatusCode}.");
            }
            if (response.Translations.Count != batch.Length)
            {
                throw new FileContentInvalidException("Translation provider returned a mismatched segment count.");
            }
            translated.AddRange(response.Translations.Select(translation => translation.TranslatedText));
        }

        return translated;
    }

    private static IEnumerable<string[]> CreateBatches(
        IReadOnlyList<string> segments,
        int maximumItems,
        int maximumCodePoints)
    {
        var batch = new List<string>(maximumItems);
        var codePoints = 0;
        foreach (var segment in segments)
        {
            var segmentLength = segment.EnumerateRunes().Count();
            if (segmentLength > maximumCodePoints)
            {
                throw new FileContentInvalidException("A transcript segment exceeds the translation size limit.");
            }
            if (batch.Count == maximumItems || codePoints + segmentLength > maximumCodePoints)
            {
                yield return batch.ToArray();
                batch.Clear();
                codePoints = 0;
            }
            batch.Add(segment);
            codePoints += segmentLength;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }
}
