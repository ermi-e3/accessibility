using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Documents;
using Accessibility.Application.Features.Processing;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Accessibility.Infrastructure.Documents;

public sealed class DocumentService(
    AccessLearnDbContext database,
    IUserContext userContext,
    IObjectStorage objectStorage,
    IDocxPackageSafetyValidator safetyValidator,
    IOpenXmlDocumentNormalizer normalizer,
    ISemanticHtmlDocumentRenderer htmlRenderer,
    IBackgroundJobScheduler scheduler,
    IOptions<DocumentProcessingOptions> documentProcessingOptions,
    TimeProvider timeProvider,
    ILogger<DocumentService> logger) : IDocumentService
{
    private const long MaximumDocumentBytes = 25L * 1024 * 1024;
    private const string PipelineVersion =
        "accesslearn-documents-v1|weasyprint-70.0|verapdf-1.30.4|noto-ethiopic-0dbccc00";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<DocumentUploadResult> UploadAsync(
        Guid lessonId,
        Accessibility.Application.Features.Documents.UploadDocumentCommand command,
        QuarantinedUpload upload,
        CancellationToken cancellationToken)
    {
        var ownerId = RequireUserId();
        var title = RequireText(command.Title, "title", 200);
        var language = NormalizeLanguage(command.Language);
        ValidateUploadMetadata(upload);

        var lesson = await VisibleLessons(ownerId)
            .SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        if (lesson.Status == LessonStatus.Archived)
        {
            throw new StateConflictException("Archived lessons cannot receive documents.");
        }

        try
        {
            await using var validationStream = new FileStream(
                upload.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                useAsync: true);
            safetyValidator.Validate(validationStream, cancellationToken);
        }
        catch (DocxPackageRejectedException exception)
        {
            throw new FileContentInvalidException(exception.Message);
        }

        var objectKey = BuildSourceObjectKey(lessonId, Guid.CreateVersion7(), upload.OriginalFileName, upload.Sha256);
        var storedFile = new StoredFile(
            objectKey,
            upload.OriginalFileName,
            upload.ContentType,
            upload.ByteLength,
            upload.Sha256);
        var document = new AccessibleDocument(lessonId, storedFile.Id, title, language);
        lesson.AdvanceDraftRevision();
        database.StoredFiles.Add(storedFile);
        database.Documents.Add(document);
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            await using var content = new FileStream(
                upload.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                useAsync: true);
            var write = await objectStorage.PutAsync(
                objectKey,
                content,
                upload.ContentType,
                upload.ByteLength,
                new Dictionary<string, string>
                {
                    ["sha256"] = upload.Sha256,
                    ["owner-id"] = ownerId.ToString("N"),
                    ["lesson-id"] = lessonId.ToString("N"),
                    ["document-id"] = document.Id.ToString("N")
                },
                cancellationToken);
            storedFile.MarkReady(write.ETag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            storedFile.Reject("UPLOAD_CANCELLED");
            document.Fail();
            await database.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            storedFile.Reject("STORAGE_UNAVAILABLE");
            document.Fail();
            await database.SaveChangesAsync(CancellationToken.None);
            throw new StorageUnavailableException("Document source storage is unavailable.", exception);
        }

        document.MarkNormalizing();
        var payload = new NormalizeDocumentPayload(
            document.Id,
            storedFile.Id,
            storedFile.Sha256,
            normalizer.Version);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var fingerprint = Sha256Hex($"normalize|{document.Id:N}|{storedFile.Sha256}|{normalizer.Version}");
        var job = new ProcessingJob(
            ProcessingJobType.NormalizeDocument,
            fingerprint,
            lessonId: lesson.Id,
            documentId: document.Id,
            requestedByUserId: ownerId,
            payloadJson: payloadJson,
            clientRequestKey: null,
            requestFingerprint: fingerprint);
        database.ProcessingJobs.Add(job);
        await database.SaveChangesAsync(cancellationToken);
        TrySchedule(job);
        await database.SaveChangesAsync(cancellationToken);

        var detail = await BuildDetailAsync(document.Id, ownerId, tracking: false, cancellationToken);
        return new DocumentUploadResult(detail, ToJobModel(job));
    }

    public Task<DocumentDetailModel> GetAsync(Guid documentId, CancellationToken cancellationToken) =>
        BuildDetailAsync(documentId, RequireUserId(), tracking: false, cancellationToken);

    public async Task<DocumentDetailModel> ReviewImageAsync(
        Guid documentId,
        Guid imageId,
        long expectedVersion,
        ReviewDocumentImageCommand command,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var document = await VisibleDocuments(userId)
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken)
            ?? throw new ResourceNotFoundException("Document", documentId);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var lockedDocument = await database.Documents
            .FromSqlInterpolated($"SELECT * FROM documents WHERE id = {documentId} FOR UPDATE")
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        if (lockedDocument.Version != expectedVersion)
        {
            throw new VersionConflictException();
        }
        if (document.NormalizedModelFileId is null)
        {
            throw new StateConflictException("The document has not been normalized.");
        }

        var images = await database.DocumentImages
            .Where(image => image.DocumentId == documentId)
            .OrderBy(image => image.Sequence)
            .ToListAsync(cancellationToken);
        var image = images.SingleOrDefault(item => item.Id == imageId)
            ?? throw new ResourceNotFoundException("Document image", imageId);
        try
        {
            image.ApproveDescription(command.AltText, command.IsDecorative, userId, timeProvider.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["altText"] = [exception.Message]
            });
        }

        var outputs = await database.DocumentOutputs
            .Where(output => output.DocumentId == documentId && output.Status != DocumentOutputStatus.Stale)
            .ToListAsync(cancellationToken);
        foreach (var output in outputs)
        {
            output.MarkStale();
        }

        var allReviewed = images.All(item => item.ReviewStatus == ReviewStatus.Approved);
        document.SetReviewReadiness(allReviewed && !HasBlockingIssues(document.IssueSummaryJson));
        var lesson = await database.Lessons.SingleAsync(item => item.Id == document.LessonId, cancellationToken);
        lesson.AdvanceDraftRevision();
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new VersionConflictException();
        }

        return await BuildDetailAsync(documentId, userId, tracking: false, cancellationToken);
    }

    public async Task<ProcessingJobModel> GenerateOutputsAsync(
        Guid documentId,
        long expectedVersion,
        GenerateDocumentOutputsCommand command,
        string? requestIdempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var document = await VisibleDocuments(userId)
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken)
            ?? throw new ResourceNotFoundException("Document", documentId);
        if (document.Version != expectedVersion)
        {
            throw new VersionConflictException();
        }
        if (document.Status != DocumentStatus.Ready || document.NormalizedModelFileId is null)
        {
            throw new StateConflictException("Document issues and image reviews must be resolved before outputs are generated.");
        }

        var formats = NormalizeFormats(command.Formats);
        var pipelineVersion = formats.Contains("pdf", StringComparer.Ordinal)
            ? $"{PipelineVersion}|pdf-config-{PdfConfigurationFingerprint(documentProcessingOptions.Value)}"
            : $"{PipelineVersion}|html-only";
        var normalizedFile = await database.StoredFiles.AsNoTracking()
            .SingleAsync(file => file.Id == document.NormalizedModelFileId.Value, cancellationToken);
        var payload = new GenerateDocumentOutputsPayload(
            document.Id,
            document.Version,
            normalizedFile.Id,
            pipelineVersion,
            formats);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var fingerprint = Sha256Hex(
            $"outputs|{document.Id:N}|{document.Version}|{normalizedFile.Sha256}|{pipelineVersion}|{string.Join(',', formats)}");
        var requestKey = NormalizeRequestKey(requestIdempotencyKey);

        if (requestKey is not null)
        {
            var replay = await database.ProcessingJobs
                .SingleOrDefaultAsync(
                    job => job.RequestedByUserId == userId && job.ClientRequestKey == requestKey,
                    cancellationToken);
            if (replay is not null)
            {
                if (!string.Equals(replay.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new StateConflictException("The idempotency key was already used for a different request.");
                }

                return ToJobModel(replay);
            }
        }

        var existing = await database.ProcessingJobs
            .SingleOrDefaultAsync(job => job.IdempotencyKey == fingerprint, cancellationToken);
        if (existing is not null)
        {
            return ToJobModel(existing);
        }

        var job = new ProcessingJob(
            ProcessingJobType.GenerateDocumentOutputs,
            fingerprint,
            lessonId: document.LessonId,
            documentId: document.Id,
            requestedByUserId: userId,
            payloadJson: payloadJson,
            clientRequestKey: requestKey,
            requestFingerprint: fingerprint);
        database.ProcessingJobs.Add(job);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            database.Entry(job).State = EntityState.Detached;
            if (requestKey is not null)
            {
                var keyedWinner = await database.ProcessingJobs.AsNoTracking().SingleOrDefaultAsync(
                    item => item.RequestedByUserId == userId && item.ClientRequestKey == requestKey,
                    cancellationToken);
                if (keyedWinner is not null)
                {
                    if (!string.Equals(keyedWinner.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        throw new StateConflictException("The idempotency key was already used for a different request.");
                    }
                    return ToJobModel(keyedWinner);
                }
            }
            var fingerprintWinner = await database.ProcessingJobs.AsNoTracking().SingleOrDefaultAsync(
                item => item.IdempotencyKey == fingerprint,
                cancellationToken);
            if (fingerprintWinner is not null)
            {
                return ToJobModel(fingerprintWinner);
            }
            throw;
        }
        TrySchedule(job);
        await database.SaveChangesAsync(cancellationToken);
        return ToJobModel(job);
    }

    public async Task<DocumentDownloadModel> OpenImageAsync(
        Guid documentId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var file = await (
                from document in VisibleDocuments(userId).AsNoTracking()
                join image in database.DocumentImages.AsNoTracking() on document.Id equals image.DocumentId
                join storedFile in database.StoredFiles.AsNoTracking() on image.StoredFileId equals storedFile.Id
                where document.Id == documentId && image.Id == imageId && storedFile.Status == StoredFileStatus.Ready
                select storedFile)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Document image", imageId);
        return new DocumentDownloadModel(
            await objectStorage.OpenReadAsync(file.ObjectKey, cancellationToken),
            file.OriginalFileName);
    }

    public async Task<DocumentDownloadModel> OpenNormalizedModelAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var file = await (
                from document in VisibleDocuments(userId).AsNoTracking()
                join storedFile in database.StoredFiles.AsNoTracking()
                    on document.NormalizedModelFileId equals storedFile.Id
                where document.Id == documentId && storedFile.Status == StoredFileStatus.Ready
                select storedFile)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Normalized document model", documentId);
        return new DocumentDownloadModel(
            await objectStorage.OpenReadAsync(file.ObjectKey, cancellationToken),
            file.OriginalFileName);
    }

    public async Task<DocumentPreviewModel> GetPreviewAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var row = await (
                from document in VisibleDocuments(userId).AsNoTracking()
                join storedFile in database.StoredFiles.AsNoTracking()
                    on document.NormalizedModelFileId equals storedFile.Id
                where document.Id == documentId && storedFile.Status == StoredFileStatus.Ready
                select new { document, storedFile })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Normalized document model", documentId);
        NormalizedDocumentModel model;
        await using (var stored = await objectStorage.OpenReadAsync(row.storedFile.ObjectKey, cancellationToken))
        {
            model = NormalizedDocumentJson.Deserialize(await ReadVerifiedUtf8Async(
                stored,
                row.storedFile,
                64L * 1024 * 1024,
                cancellationToken));
        }
        var images = await database.DocumentImages.AsNoTracking()
            .Where(image => image.DocumentId == documentId && image.ReviewStatus == ReviewStatus.Approved)
            .OrderBy(image => image.Sequence)
            .Select(image => new ReviewedImageDecision(
                $"image-{image.Sequence + 1}",
                image.AltText,
                image.IsDecorative))
            .ToArrayAsync(cancellationToken);
        var preview = htmlRenderer.RenderPreview(model, images);
        return new DocumentPreviewModel(preview.Content, preview.ContentType, model.Issues);
    }

    public async Task<IReadOnlyList<DocumentOutputModel>> ListOutputsAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var documentVersion = await VisibleDocuments(userId)
            .Where(document => document.Id == documentId)
            .Select(document => (long?)document.Version)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Document", documentId);

        var outputs = await database.DocumentOutputs.AsNoTracking()
            .Where(output => output.DocumentId == documentId)
            .OrderBy(output => output.Kind)
            .ThenByDescending(output => output.CreatedAt)
            .ToListAsync(cancellationToken);
        return outputs.Select(output => ToOutputModel(output, documentVersion)).ToArray();
    }

    public async Task<DocumentDownloadModel> OpenOutputAsync(
        Guid documentId,
        Guid outputId,
        bool validationReport,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var result = await (
                from document in VisibleDocuments(userId).AsNoTracking()
                join output in database.DocumentOutputs.AsNoTracking() on document.Id equals output.DocumentId
                where document.Id == documentId && output.Id == outputId
                select new { document, output })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Document output", outputId);
        if (!validationReport && result.output.Status is not (
                DocumentOutputStatus.Generated or
                DocumentOutputStatus.Validated or
                DocumentOutputStatus.ValidationFailed))
        {
            throw new ResourceNotFoundException("Document output", outputId);
        }
        var fileId = validationReport
            ? result.output.ValidationReportFileId
            : result.output.StoredFileId;
        if (fileId is null)
        {
            throw new ResourceNotFoundException(validationReport ? "Validation report" : "Document output", outputId);
        }

        var file = await database.StoredFiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == fileId.Value && item.Status == StoredFileStatus.Ready, cancellationToken)
            ?? throw new ResourceNotFoundException("Stored document output", fileId.Value);
        var read = await objectStorage.OpenReadAsync(file.ObjectKey, cancellationToken);
        return new DocumentDownloadModel(read, file.OriginalFileName);
    }

    private async Task<DocumentDetailModel> BuildDetailAsync(
        Guid documentId,
        Guid userId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var documents = VisibleDocuments(userId);
        if (!tracking)
        {
            documents = documents.AsNoTracking();
        }

        var row = await (
                from document in documents
                join file in database.StoredFiles.AsNoTracking() on document.SourceFileId equals file.Id
                where document.Id == documentId
                select new { document, file })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Document", documentId);
        var images = await database.DocumentImages.AsNoTracking()
            .Where(image => image.DocumentId == documentId)
            .OrderBy(image => image.Sequence)
            .ToListAsync(cancellationToken);
        var outputs = await database.DocumentOutputs.AsNoTracking()
            .Where(output => output.DocumentId == documentId)
            .OrderBy(output => output.Kind)
            .ThenByDescending(output => output.CreatedAt)
            .ToListAsync(cancellationToken);
        var issues = DeserializeIssues(row.document.IssueSummaryJson);
        return new DocumentDetailModel(
            row.document.Id,
            row.document.LessonId,
            row.document.Title,
            row.document.Language,
            Contract(row.document.Status),
            row.file.OriginalFileName,
            row.file.ByteLength,
            row.file.Sha256,
            row.document.NormalizerVersion,
            row.document.NormalizedAt,
            issues,
            images.Select(ToImageModel).ToArray(),
            outputs.Select(output => ToOutputModel(output, row.document.Version)).ToArray(),
            row.document.Status == DocumentStatus.Ready &&
            !issues.Any(issue => issue.Severity == DocumentIssueSeverity.Blocking) &&
            images.All(image => image.ReviewStatus == ReviewStatus.Approved),
            row.document.Version);
    }

    private IQueryable<Lesson> VisibleLessons(Guid userId)
    {
        var query = database.Lessons.AsQueryable();
        return userContext.IsInRole(AccessLearnRoles.Administrator)
            ? query
            : query.Where(lesson => lesson.OwnerUserId == userId);
    }

    private IQueryable<AccessibleDocument> VisibleDocuments(Guid userId)
    {
        var query = database.Documents.Join(
            database.Lessons,
            document => document.LessonId,
            lesson => lesson.Id,
            (document, lesson) => new { document, lesson });
        if (!userContext.IsInRole(AccessLearnRoles.Administrator))
        {
            query = query.Where(item => item.lesson.OwnerUserId == userId);
        }

        return query.Select(item => item.document);
    }

    private void TrySchedule(ProcessingJob job)
    {
        try
        {
            job.SetExternalJobId(scheduler.Enqueue(job.Id));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Document job {JobId} remains queued for reconciliation", job.Id);
        }
    }

    private Guid RequireUserId() => userContext.UserId
        ?? throw new UnauthorizedAccessException("A user identity is required.");

    private static void ValidateUploadMetadata(QuarantinedUpload upload)
    {
        var extension = Path.GetExtension(upload.OriginalFileName);
        if (!extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedFileTypeException(extension);
        }
        if (!upload.ContentType.Equals(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentTypeMismatchException(upload.ContentType);
        }
        if (upload.ByteLength <= 0 || upload.ByteLength > MaximumDocumentBytes)
        {
            throw new FileTooLargeException(MaximumDocumentBytes);
        }
        if (upload.Header.Length < 4 || upload.Header[0] != 0x50 || upload.Header[1] != 0x4B ||
            upload.Header[2] != 0x03 || upload.Header[3] != 0x04)
        {
            throw new FileContentInvalidException("The DOCX ZIP signature is invalid.");
        }
    }

    private static string RequireText(string value, string field, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} is required and must not exceed {maximumLength} characters."]
            });
        }

        return normalized;
    }

    private static string NormalizeLanguage(string language)
    {
        try
        {
            return DocumentLanguagePolicy.Normalize(language);
        }
        catch (ArgumentException exception)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["language"] = [exception.Message]
            });
        }
    }

    private static string[] NormalizeFormats(IReadOnlyList<string> formats)
    {
        if (formats is null || formats.Count == 0)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["formats"] = ["At least one output format is required."]
            });
        }

        var normalized = formats
            .Select(format => format.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(format => format, StringComparer.Ordinal)
            .ToList();
        if (normalized.Any(format => format is not ("html" or "pdf")))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["formats"] = ["Only 'html' and 'pdf' are supported."]
            });
        }
        if (normalized.Contains("pdf", StringComparer.Ordinal) && !normalized.Contains("html", StringComparer.Ordinal))
        {
            normalized.Insert(0, "html");
        }

        return normalized.ToArray();
    }

    private static string? NormalizeRequestKey(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }
        if (normalized.Length > 200)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["Idempotency-Key must not exceed 200 characters."]
            });
        }

        return normalized;
    }

    private static bool HasBlockingIssues(string? json) =>
        DeserializeIssues(json).Any(issue => issue.Severity == DocumentIssueSeverity.Blocking);

    private static IReadOnlyList<DocumentIssue> DeserializeIssues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<DocumentIssue[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [new DocumentIssue(0, "ISSUE_SUMMARY_INVALID", DocumentIssueSeverity.Blocking,
                "The normalized issue summary is invalid.", "document")];
        }
    }

    private static DocumentImageModel ToImageModel(DocumentImage image) => new(
        image.Id,
        image.Sequence,
        image.RelationshipId,
        image.SourceLocation,
        image.SuggestedAltText,
        image.SuggestedDecorative,
        image.AltText,
        image.IsDecorative,
        Contract(image.ReviewStatus),
        image.ReviewedByUserId,
        image.ReviewedAt,
        image.Version);

    private static DocumentOutputModel ToOutputModel(DocumentOutput output, long currentDocumentVersion) => new(
        output.Id,
        Contract(output.Kind),
        Contract(output.Status),
        OutputLabel(output),
        output.SourceRevision,
        output.GeneratorVersion,
        output.ValidatorName,
        output.ValidatorVersion,
        output.ValidationProfile,
        output.ValidatedAt,
        output.SourceRevision == currentDocumentVersion &&
        output.Status is (DocumentOutputStatus.Generated or DocumentOutputStatus.Validated or DocumentOutputStatus.ValidationFailed),
        output.ValidationReportFileId is not null,
        output.Version);

    private static string OutputLabel(DocumentOutput output) => (output.Kind, output.Status) switch
    {
        (DocumentOutputKind.Html, DocumentOutputStatus.Validated) => "semanticHtmlChecksPassed",
        (DocumentOutputKind.Html, DocumentOutputStatus.ValidationFailed) => "semanticHtmlChecksFailed",
        (DocumentOutputKind.Pdf, DocumentOutputStatus.Validated) => "veraPdfUa1ProfilePassed",
        (DocumentOutputKind.Pdf, DocumentOutputStatus.ValidationFailed) => "taggedDraft",
        (_, DocumentOutputStatus.Generated) => "generatedNotValidated",
        (_, DocumentOutputStatus.Stale) => "stale",
        _ => "generationFailed"
    };

    private static ProcessingJobModel ToJobModel(ProcessingJob job) => new(
        job.Id,
        job.LessonId,
        job.MediaAssetId,
        job.DocumentId,
        Contract(job.Type),
        Contract(job.Status),
        job.ProgressPercentage,
        job.Attempts,
        job.Provider,
        job.ErrorCode,
        job.ErrorMessage,
        job.StartedAt,
        job.CompletedAt,
        job.CancellationRequestedAt,
        job.Version,
        job.Attempts < 4 &&
        (job.Status == ProcessingJobStatus.Cancelled ||
         job.Status == ProcessingJobStatus.Failed && job.ErrorCode is
             "PROCESSOR_UNAVAILABLE" or "STORAGE_UNAVAILABLE" or "LEASE_EXPIRED"));

    private static string BuildSourceObjectKey(Guid lessonId, Guid uploadId, string fileName, string sha256) =>
        $"lessons/{lessonId:N}/documents/{uploadId:N}/{sha256[..16]}{Path.GetExtension(fileName).ToLowerInvariant()}";

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string PdfConfigurationFingerprint(DocumentProcessingOptions options) => Sha256Hex(string.Join('|',
        options.WeasyPrintExecutablePath,
        options.ExpectedWeasyPrintVersion,
        options.JavaExecutablePath,
        options.ExpectedJavaMajorVersion,
        options.VeraPdfJarPath,
        options.ExpectedVeraPdfVersion,
        options.VeraPdfJarSha256,
        options.VeraPdfProfile,
        options.NotoEthiopicFontPath,
        options.NotoEthiopicFontSha256));

    private static string Contract<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static async Task<string> ReadVerifiedUtf8Async(
        ObjectStorageReadResult resource,
        StoredFile expected,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (resource.ByteLength != expected.ByteLength)
        {
            throw new FileContentInvalidException("The normalized document model has an unexpected byte length.");
        }
        using var content = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await resource.Content.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > expected.ByteLength || total > maximumBytes)
            {
                throw new FileContentInvalidException("The normalized document model exceeds the supported size.");
            }
            hash.AppendData(buffer, 0, read);
            content.Write(buffer, 0, read);
        }
        if (total != expected.ByteLength ||
            !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected.Sha256, StringComparison.Ordinal))
        {
            throw new FileContentInvalidException("The normalized document model failed integrity verification.");
        }
        return Encoding.UTF8.GetString(content.ToArray());
    }
}
