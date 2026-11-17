using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Documents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Accessibility.Infrastructure.Documents;

public interface IDocumentJobExecutor
{
    string ProviderName(ProcessingJobType type);

    Task ExecuteAsync(
        ProcessingJob job,
        string workspace,
        Func<int, Task> reportProgress,
        CancellationToken cancellationToken);
}

public sealed class DocumentJobExecutor(
    AccessLearnDbContext database,
    IObjectStorage objectStorage,
    IDocxPackageSafetyValidator safetyValidator,
    IOpenXmlDocumentNormalizer normalizer,
    ISemanticHtmlDocumentRenderer htmlRenderer,
    IServiceProvider services,
    TimeProvider timeProvider) : IDocumentJobExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public string ProviderName(ProcessingJobType type) => type switch
    {
        ProcessingJobType.NormalizeDocument => normalizer.Version,
        ProcessingJobType.GenerateDocumentOutputs => htmlRenderer.Version,
        _ => "document-pipeline"
    };

    public Task ExecuteAsync(
        ProcessingJob job,
        string workspace,
        Func<int, Task> reportProgress,
        CancellationToken cancellationToken) => job.Type switch
        {
            ProcessingJobType.NormalizeDocument => NormalizeAsync(job, workspace, reportProgress, cancellationToken),
            ProcessingJobType.GenerateDocumentOutputs => GenerateOutputsAsync(job, workspace, reportProgress, cancellationToken),
            _ => throw new StateConflictException("The processing job is not a document job.")
        };

    private async Task NormalizeAsync(
        ProcessingJob job,
        string workspace,
        Func<int, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<NormalizeDocumentPayload>(job.PayloadJson ?? string.Empty, JsonOptions)
            ?? throw new FileContentInvalidException("The normalization job payload is invalid.");
        var row = await (
                from document in database.Documents
                join file in database.StoredFiles on document.SourceFileId equals file.Id
                where document.Id == payload.DocumentId && file.Id == payload.SourceFileId
                select new { Document = document, File = file })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Document", payload.DocumentId);
        if (!string.Equals(row.File.Sha256, payload.SourceSha256, StringComparison.Ordinal) ||
            !string.Equals(payload.NormalizerVersion, normalizer.Version, StringComparison.Ordinal))
        {
            throw new StateConflictException("The document source or normalizer changed before normalization.");
        }
        if (row.File.Status != StoredFileStatus.Ready)
        {
            throw new StateConflictException("The document source is not ready.");
        }

        var sourcePath = Path.Combine(workspace, "source.docx");
        await using (var stored = await objectStorage.OpenReadAsync(row.File.ObjectKey, cancellationToken))
        await using (var destination = new FileStream(
            sourcePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await CopyAndVerifyAsync(stored, row.File, destination, cancellationToken);
        }
        await reportProgress(20);

        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        safetyValidator.Validate(source, cancellationToken);
        source.Position = 0;
        var model = normalizer.Normalize(
            source,
            new DocumentNormalizationOptions(row.Document.Title, row.Document.Language),
            cancellationToken);
        await reportProgress(55);

        var modelBytes = Encoding.UTF8.GetBytes(NormalizedDocumentJson.Serialize(model));
        var modelFile = await PersistBytesAsync(
            $"lessons/{row.Document.LessonId:N}/documents/{row.Document.Id:N}/normalized/{job.Id:N}.json",
            "normalized-document.json",
            "application/json",
            modelBytes,
            job.Id,
            cancellationToken);
        var existingImages = await database.DocumentImages
            .Where(image => image.DocumentId == row.Document.Id)
            .OrderBy(image => image.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var image in model.Images.OrderBy(image => image.Order))
        {
            if (existingImages.Any(existing => existing.Sequence == image.Order))
            {
                continue;
            }
            var extension = image.MediaType == "image/png" ? ".png" : ".jpg";
            var imageFile = await PersistBytesAsync(
                $"lessons/{row.Document.LessonId:N}/documents/{row.Document.Id:N}/images/{image.Order:D4}{extension}",
                $"document-image-{image.Order + 1}{extension}",
                image.MediaType,
                image.Payload.ToArray(),
                job.Id,
                cancellationToken);
            database.DocumentImages.Add(new DocumentImage(
                row.Document.Id,
                imageFile.Id,
                image.Order,
                image.RelationshipId,
                image.SourceLocation,
                image.SuggestedAltText,
                image.SuggestedDecorative));
        }

        var issueJson = JsonSerializer.Serialize(model.Issues, JsonOptions);
        row.Document.MarkNormalized(
            modelFile.Id,
            normalizer.Version,
            issueJson,
            model.Images.Count > 0 || model.HasBlockingIssues,
            timeProvider.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);
        await reportProgress(90);
    }

    private async Task GenerateOutputsAsync(
        ProcessingJob job,
        string workspace,
        Func<int, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<GenerateDocumentOutputsPayload>(job.PayloadJson ?? string.Empty, JsonOptions)
            ?? throw new FileContentInvalidException("The document output job payload is invalid.");
        var document = await database.Documents.SingleOrDefaultAsync(
            item => item.Id == payload.DocumentId,
            cancellationToken) ?? throw new ResourceNotFoundException("Document", payload.DocumentId);
        if (document.Status != DocumentStatus.Ready || document.Version != payload.SourceRevision ||
            document.NormalizedModelFileId != payload.NormalizedModelFileId)
        {
            throw new StateConflictException("The reviewed document changed before output generation.");
        }
        var pipelineToken = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload.PipelineVersion)))[..16];

        var modelFile = await database.StoredFiles.AsNoTracking().SingleAsync(
            file => file.Id == payload.NormalizedModelFileId && file.Status == StoredFileStatus.Ready,
            cancellationToken);
        NormalizedDocumentModel model;
        await using (var stored = await objectStorage.OpenReadAsync(modelFile.ObjectKey, cancellationToken))
        {
            model = NormalizedDocumentJson.Deserialize(await ReadVerifiedUtf8Async(
                stored,
                modelFile,
                64L * 1024 * 1024,
                cancellationToken));
        }
        var images = await database.DocumentImages.AsNoTracking()
            .Where(image => image.DocumentId == document.Id)
            .OrderBy(image => image.Sequence)
            .ToListAsync(cancellationToken);
        if (images.Count != model.Images.Count || images.Any(image => image.ReviewStatus != ReviewStatus.Approved))
        {
            throw new StateConflictException("Every image occurrence must have a reviewed alternative or decorative decision.");
        }
        var decisions = images.Select(image => new ReviewedImageDecision(
            $"image-{image.Sequence + 1}",
            image.AltText,
            image.IsDecorative)).ToArray();
        var html = htmlRenderer.Render(model, decisions);
        await reportProgress(30);

        var htmlBytes = Encoding.UTF8.GetBytes(html.Content);
        if (htmlBytes.LongLength > 50L * 1024 * 1024)
        {
            throw new FileContentInvalidException("Generated semantic HTML exceeds the supported size.");
        }
        var htmlFile = await PersistBytesAsync(
            $"lessons/{document.LessonId:N}/documents/{document.Id:N}/outputs/{payload.SourceRevision}/{pipelineToken}/document.html",
            "document.html",
            html.ContentType,
            htmlBytes,
            job.Id,
            cancellationToken);
        var htmlReportJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            validator = htmlRenderer.Version,
            passed = html.Validation.IsValid,
            issues = html.Validation.Issues
        }, JsonOptions);
        var htmlReport = await PersistBytesAsync(
            $"lessons/{document.LessonId:N}/documents/{document.Id:N}/outputs/{payload.SourceRevision}/{pipelineToken}/html-validation.json",
            "html-validation.json",
            "application/json",
            Encoding.UTF8.GetBytes(htmlReportJson),
            job.Id,
            cancellationToken);
        var draftAdvanced = false;
        await using (var htmlActivation = await LockCurrentDocumentAsync(
                         document.Id,
                         payload.SourceRevision,
                         cancellationToken))
        {
            var htmlOutput = await UpsertOutputAsync(
                document,
                DocumentOutputKind.Html,
                htmlFile,
                $"{htmlRenderer.Version}-{pipelineToken}",
                cancellationToken);
            if (htmlOutput.Status == DocumentOutputStatus.Generated)
            {
                htmlOutput.RecordValidation(
                    htmlReport.Id,
                    html.Validation.IsValid,
                    "AccessLearn semantic HTML validator",
                    htmlRenderer.Version,
                    "semantic-html-v1",
                    htmlReportJson,
                    timeProvider.GetUtcNow());
                await database.SaveChangesAsync(cancellationToken);
                if (html.Validation.IsValid)
                {
                    await AdvanceLessonDraftAsync(document.LessonId, cancellationToken);
                    draftAdvanced = true;
                }
            }
            await htmlActivation.CommitAsync(cancellationToken);
        }
        if (!html.Validation.IsValid)
        {
            throw new FileContentInvalidException("Generated semantic HTML failed validation.");
        }
        await reportProgress(55);

        if (!payload.Formats.Contains("pdf", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        IDocumentPdfToolchain pdfToolchain;
        try
        {
            pdfToolchain = services.GetRequiredService<IDocumentPdfToolchain>();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            await RecordPdfOperationalFailureAsync(
                document,
                payload,
                job,
                $"weasyprint-70.0-{pipelineToken}",
                "not-run",
                "not-run",
                "not-run",
                "The PDF toolchain is not configured.",
                cancellationToken);
            throw new ProcessorUnavailableException("The PDF toolchain is not configured.", exception);
        }
        var xhtmlPath = Path.Combine(workspace, "document.xhtml");
        var pdfPath = Path.Combine(workspace, "document.pdf");
        var reportPath = Path.Combine(workspace, "verapdf-report.json");
        await File.WriteAllTextAsync(xhtmlPath, html.Content, new UTF8Encoding(false), cancellationToken);
        var pdf = await pdfToolchain.GenerateAndValidateAsync(
            new DocumentPdfRequest(workspace, xhtmlPath, pdfPath, reportPath),
            cancellationToken);
        if (pdf.Outcome == DocumentPdfOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (pdf.Outcome is not (DocumentPdfOutcome.PassedProfile or DocumentPdfOutcome.TaggedDraft) ||
            pdf.PdfPath is null || pdf.ValidationReportPath is null)
        {
            await RecordPdfOperationalFailureAsync(
                document,
                payload,
                job,
                $"{pdfToolchain.RendererName}-{pdf.RendererVersion ?? pdfToolchain.RequiredRendererVersion}-{pipelineToken}",
                pdf.ValidatorVersion is null ? "not-run" : pdfToolchain.ValidatorName,
                pdf.ValidatorVersion ?? "not-run",
                pdf.ValidationProfile ?? "not-run",
                pdf.FailureReason ?? "The PDF toolchain could not complete.",
                cancellationToken);
            if (pdf.Outcome is DocumentPdfOutcome.MalformedPdfOutput or
                DocumentPdfOutcome.PdfOutputTooLarge or
                DocumentPdfOutcome.MalformedValidationReport or
                DocumentPdfOutcome.ValidationReportTooLarge or
                DocumentPdfOutcome.ExternalProcessOutputTooLarge)
            {
                throw new FileContentInvalidException(
                    pdf.FailureReason ?? "The PDF toolchain produced invalid or oversized output.");
            }
            throw new ProcessorUnavailableException(pdf.FailureReason ?? "The PDF toolchain could not complete.");
        }

        var pdfBytes = await File.ReadAllBytesAsync(pdf.PdfPath, cancellationToken);
        var reportBytes = await File.ReadAllBytesAsync(pdf.ValidationReportPath, cancellationToken);
        var pdfFile = await PersistBytesAsync(
            $"lessons/{document.LessonId:N}/documents/{document.Id:N}/outputs/{payload.SourceRevision}/{pipelineToken}/document.pdf",
            "document.pdf",
            "application/pdf",
            pdfBytes,
            job.Id,
            cancellationToken);
        var reportFile = await PersistBytesAsync(
            $"lessons/{document.LessonId:N}/documents/{document.Id:N}/outputs/{payload.SourceRevision}/{pipelineToken}/verapdf-report.json",
            "verapdf-report.json",
            "application/json",
            reportBytes,
            job.Id,
            cancellationToken);
        await using var pdfActivation = await LockCurrentDocumentAsync(
            document.Id,
            payload.SourceRevision,
            cancellationToken);
        var output = await UpsertOutputAsync(
            document,
            DocumentOutputKind.Pdf,
            pdfFile,
            $"{pdfToolchain.RendererName}-{pdf.RendererVersion}-{pipelineToken}",
            cancellationToken);
        if (output.Status == DocumentOutputStatus.Generated)
        {
            output.RecordValidation(
                reportFile.Id,
                pdf.Outcome == DocumentPdfOutcome.PassedProfile,
                pdfToolchain.ValidatorName,
                pdf.ValidatorVersion ?? pdfToolchain.RequiredValidatorVersion,
                pdf.ValidationProfile ?? pdfToolchain.RequiredValidationProfile,
                JsonSerializer.Serialize(new
                {
                    outcome = pdf.Outcome,
                    rendererVersion = pdf.RendererVersion,
                    validatorVersion = pdf.ValidatorVersion,
                    validationProfile = pdf.ValidationProfile,
                    failureReason = pdf.FailureReason
                }, JsonOptions),
                timeProvider.GetUtcNow());
            await database.SaveChangesAsync(cancellationToken);
            if (pdf.Outcome == DocumentPdfOutcome.PassedProfile && !draftAdvanced)
            {
                await AdvanceLessonDraftAsync(document.LessonId, cancellationToken);
            }
        }
        await pdfActivation.CommitAsync(cancellationToken);
        await reportProgress(90);
    }

    private async Task<DocumentOutput> UpsertOutputAsync(
        AccessibleDocument document,
        DocumentOutputKind kind,
        StoredFile storedFile,
        string generatorVersion,
        CancellationToken cancellationToken)
    {
        var existing = await database.DocumentOutputs.SingleOrDefaultAsync(output =>
            output.DocumentId == document.Id && output.SourceRevision == document.Version &&
            output.Kind == kind && output.GeneratorVersion == generatorVersion,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Status == DocumentOutputStatus.Failed && storedFile.ContentType == "application/pdf")
            {
                existing.RetryGeneration(storedFile.Id, generatorVersion);
                await database.SaveChangesAsync(cancellationToken);
            }
            return existing;
        }

        var output = new DocumentOutput(document.Id, document.Version, kind, storedFile.Id, generatorVersion);
        database.DocumentOutputs.Add(output);
        await database.SaveChangesAsync(cancellationToken);
        return output;
    }

    private async Task RecordPdfOperationalFailureAsync(
        AccessibleDocument document,
        GenerateDocumentOutputsPayload payload,
        ProcessingJob job,
        string generatorVersion,
        string validatorName,
        string validatorVersion,
        string validationProfile,
        string reason,
        CancellationToken cancellationToken)
    {
        var summary = JsonSerializer.Serialize(new
        {
            outcome = "operationalFailure",
            reason
        }, JsonOptions);
        var report = await PersistBytesAsync(
            $"lessons/{document.LessonId:N}/documents/{document.Id:N}/outputs/{payload.SourceRevision}/failures/{job.Id:N}.json",
            "pdf-generation-failure.json",
            "application/json",
            Encoding.UTF8.GetBytes(summary),
            job.Id,
            cancellationToken);
        var output = await UpsertOutputAsync(
            document,
            DocumentOutputKind.Pdf,
            report,
            generatorVersion,
            cancellationToken);
        if (output.Status == DocumentOutputStatus.Generated)
        {
            output.RecordFailure(
                report.Id,
                validatorName,
                validatorVersion,
                validationProfile,
                summary,
                timeProvider.GetUtcNow());
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task AdvanceLessonDraftAsync(Guid lessonId, CancellationToken cancellationToken)
    {
        var lesson = await database.Lessons.SingleAsync(item => item.Id == lessonId, cancellationToken);
        lesson.AdvanceDraftRevision();
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<IDbContextTransaction> LockCurrentDocumentAsync(
        Guid documentId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var locked = await database.Documents
                .FromSqlInterpolated($"SELECT * FROM documents WHERE id = {documentId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new ResourceNotFoundException("Document", documentId);
            if (locked.Version != expectedVersion || locked.Status != DocumentStatus.Ready)
            {
                throw new StateConflictException("The reviewed document changed before output activation.");
            }
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<StoredFile> PersistBytesAsync(
        string objectKey,
        string fileName,
        string contentType,
        byte[] content,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var existing = await database.StoredFiles.SingleOrDefaultAsync(file => file.ObjectKey == objectKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Sha256 != sha256 || existing.ByteLength != content.LongLength)
            {
                throw new StateConflictException("A document artifact key already contains different content.");
            }
            if (existing.Status == StoredFileStatus.Ready)
            {
                return existing;
            }
            if (existing.Status == StoredFileStatus.Rejected)
            {
                existing.RetryQuarantine();
            }
        }
        else
        {
            existing = new StoredFile(objectKey, fileName, contentType, content.LongLength, sha256);
            database.StoredFiles.Add(existing);
            await database.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await using var stream = new MemoryStream(content, writable: false);
            var write = await objectStorage.PutAsync(
                objectKey,
                stream,
                contentType,
                content.LongLength,
                new Dictionary<string, string>
                {
                    ["sha256"] = sha256,
                    ["processing-job-id"] = jobId.ToString("N")
                },
                cancellationToken);
            existing.MarkReady(write.ETag);
            await database.SaveChangesAsync(cancellationToken);
            return existing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            existing.Reject("Document artifact storage was cancelled.");
            await database.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            existing.Reject("Document artifact storage did not complete.");
            await database.SaveChangesAsync(CancellationToken.None);
            throw new StorageUnavailableException("Document artifact storage is unavailable.", exception);
        }
    }

    private static async Task<string> ReadVerifiedUtf8Async(
        ObjectStorageReadResult resource,
        StoredFile expected,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        await CopyAndVerifyAsync(resource, expected, content, cancellationToken, maximumBytes);
        return Encoding.UTF8.GetString(content.ToArray());
    }

    private static async Task CopyAndVerifyAsync(
        ObjectStorageReadResult resource,
        StoredFile expected,
        Stream destination,
        CancellationToken cancellationToken,
        long? maximumBytes = null)
    {
        if (resource.ByteLength != expected.ByteLength)
        {
            throw new FileContentInvalidException("A stored document artifact has an unexpected byte length.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await resource.Content.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > expected.ByteLength || maximumBytes.HasValue && total > maximumBytes.Value)
            {
                throw new FileContentInvalidException("A stored document artifact exceeds the expected size.");
            }
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total != expected.ByteLength ||
            !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected.Sha256, StringComparison.Ordinal))
        {
            throw new FileContentInvalidException("A stored document artifact failed integrity verification.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
