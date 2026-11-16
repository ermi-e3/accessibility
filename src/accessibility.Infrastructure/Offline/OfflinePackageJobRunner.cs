using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Offline;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Accessibility.Infrastructure.Offline;

public sealed class OfflinePackageJobRunner(
    AccessLearnDbContext database,
    IObjectStorage storage,
    IOfflinePackageScheduler scheduler,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<OfflineLearningOptions> options,
    ILogger<OfflinePackageJobRunner> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OfflineLearningOptions _options = options.Value;

    [Queue("processing")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid offlinePackageId)
    {
        var claim = await TryClaimAsync(offlinePackageId);
        if (claim is null) return;
        var (package, leaseId) = claim.Value;
        using var executionCancellation = new CancellationTokenSource();
        var cancellationToken = executionCancellation.Token;
        var heartbeat = RunHeartbeatAsync(package.Id, leaseId, executionCancellation);
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "AccessLearn",
            "offline",
            package.Id.ToString("N"),
            $"attempt-{package.Attempts}-{leaseId:N}");
        var zipPath = workspace + ".zip";
        try
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            Directory.CreateDirectory(workspace);
            RestrictWorkspacePermissions(workspace);
            var publication = await (
                    from candidate in database.LessonPublications
                    join lesson in database.Lessons on candidate.LessonId equals lesson.Id
                    join publicationManifestFile in database.StoredFiles on candidate.ManifestFileId equals publicationManifestFile.Id
                    where candidate.Id == package.LessonPublicationId && publicationManifestFile.Status == StoredFileStatus.Ready
                    select new { Publication = candidate, Lesson = lesson, ManifestFile = publicationManifestFile })
                .SingleAsync();
            var snapshot = await LoadPublicationSnapshotAsync(
                publication.ManifestFile,
                publication.Publication,
                cancellationToken);
            SourceAsset? offlineAudio = snapshot.OfflineAudio is null
                ? null
                : new SourceAsset(
                    snapshot.OfflineAudio.ObjectKey,
                    snapshot.OfflineAudio.ContentType,
                    snapshot.OfflineAudio.ByteLength,
                    snapshot.OfflineAudio.Sha256);
            if (offlineAudio is null &&
                snapshot.Primary.ContentType is "audio/ogg" or "audio/mp4" &&
                snapshot.Primary.ByteLength <= 52_428_800)
            {
                offlineAudio = new SourceAsset(
                    snapshot.Primary.ObjectKey,
                    snapshot.Primary.ContentType,
                    snapshot.Primary.ByteLength,
                    snapshot.Primary.Sha256);
            }
            if (offlineAudio is null)
            {
                throw new StateConflictException("A ready low-bandwidth audio rendition is required for offline packaging.");
            }

            var pinnedBytes = offlineAudio.ByteLength +
                              snapshot.Captions.Sum(caption => caption.ByteLength) +
                              (snapshot.Documents ?? []).Sum(document => document.ByteLength) +
                              snapshot.Captions.Sum(caption => caption.Segments.Sum(segment =>
                                  Encoding.UTF8.GetByteCount(segment.Text) +
                                  Encoding.UTF8.GetByteCount(segment.SpeakerLabel ?? string.Empty) + 4)) +
                              1_048_576;
            if (pinnedBytes > _options.MaximumPackageBytes)
            {
                throw new StateConflictException("Pinned publication resources exceed the lightweight package size limit.");
            }

            var drafts = new List<PackageAssetDraft>();
            var audioExtension = offlineAudio.ContentType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? ".ogg" : ".m4a";
            drafts.Add(await CopySourceAsync(
                offlineAudio,
                workspace,
                $"audio/lesson{audioExtension}",
                "offlineAudio",
                snapshot.SourceLanguage,
                required: true,
                cancellationToken));
            foreach (var caption in snapshot.Captions)
            {
                drafts.Add(await CopySourceAsync(
                    new SourceAsset(
                        caption.ObjectKey,
                        "text/vtt; charset=utf-8",
                        caption.ByteLength,
                        caption.Sha256),
                    workspace,
                    $"captions/{caption.Language}.vtt",
                    "caption",
                    caption.Language,
                    required: true,
                    cancellationToken));
                var transcriptPath = Path.Combine(workspace, "transcripts", $"{caption.Language}.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
                await File.WriteAllTextAsync(
                    transcriptPath,
                    string.Join("\n", caption.Segments.Select(segment =>
                        segment.SpeakerLabel is null ? segment.Text : $"{segment.SpeakerLabel}: {segment.Text}")),
                    new UTF8Encoding(false));
                drafts.Add(await DraftAsync(
                    transcriptPath,
                    $"transcripts/{caption.Language}.txt",
                    "text/plain; charset=utf-8",
                    "transcript",
                    caption.Language,
                    required: true,
                    cancellationToken));
            }

            var documents = (snapshot.Documents ?? [])
                .OrderBy(document => document.Title, StringComparer.Ordinal)
                .ThenBy(document => document.DocumentId)
                .ToArray();
            foreach (var document in documents)
            {
                drafts.Add(await CopyDocumentAsync(
                    new SourceAsset(document.ObjectKey, document.ContentType, document.ByteLength, document.Sha256),
                    workspace,
                    $"documents/{document.DocumentId:N}-{document.SourceRevision}.html",
                    document.Language,
                    document.Title,
                    cancellationToken));
            }

            var stylePath = Path.Combine(workspace, "styles.css");
            await File.WriteAllTextAsync(stylePath, PortableStyles, new UTF8Encoding(false));
            drafts.Add(await DraftAsync(
                stylePath, "styles.css", "text/css; charset=utf-8", "shell", null, true, cancellationToken));
            var lessonPath = Path.Combine(workspace, "lesson.json");
            var lessonJson = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0",
                lessonId = publication.Lesson.Id,
                publicationId = publication.Publication.Id,
                revision = publication.Publication.Revision,
                title = snapshot.Title,
                sourceLanguage = snapshot.SourceLanguage,
                languages = snapshot.Captions.Select(caption => caption.Language).ToArray()
            }, JsonOptions);
            await File.WriteAllBytesAsync(lessonPath, lessonJson);
            drafts.Add(await DraftAsync(
                lessonPath, "lesson.json", "application/json", "metadata", null, true, cancellationToken));
            var indexPath = Path.Combine(workspace, "index.html");
            await File.WriteAllTextAsync(
                indexPath,
                BuildPortableHtml(snapshot.Title, snapshot, drafts, documents),
                new UTF8Encoding(false));
            drafts.Add(await DraftAsync(
                indexPath, "index.html", "text/html; charset=utf-8", "shell", null, true, cancellationToken));

            var preflightBytes = drafts.Sum(draft => draft.ByteLength);
            if (preflightBytes > _options.MaximumPackageBytes)
            {
                throw new StateConflictException("The lightweight offline package exceeds its configured size limit.");
            }
            var packageAssets = new List<InternalAsset>(drafts.Count);
            for (var index = 0; index < drafts.Count; index++)
            {
                packageAssets.Add(await PersistPackageAssetAsync(
                    package, drafts[index], index, cancellationToken));
            }
            var totalBytes = packageAssets.Sum(asset => asset.ByteLength);

            var publicManifest = new PortableManifest(
                "1.0",
                package.Id,
                publication.Lesson.Id,
                publication.Publication.Id,
                publication.Publication.Revision,
                snapshot.Title,
                "lightweight",
                totalBytes,
                packageAssets.Select(asset => new PortableAsset(
                    asset.Sequence,
                    asset.Path,
                    asset.ContentType,
                    asset.ByteLength,
                    asset.Sha256,
                    asset.Role,
                    asset.Language,
                    asset.IsRequired,
                    asset.DisplayName)).ToArray());
            var portableManifestPath = Path.Combine(workspace, "package-manifest.json");
            await File.WriteAllBytesAsync(portableManifestPath, JsonSerializer.SerializeToUtf8Bytes(publicManifest, JsonOptions));
            var manifestDraft = await DraftAsync(
                portableManifestPath,
                "package-manifest.json",
                "application/json",
                "manifest",
                null,
                true,
                cancellationToken);
            var manifestFile = await PersistStandaloneFileAsync(
                package,
                manifestDraft,
                $"offline/{package.Id:N}/manifest.json",
                cancellationToken);

            if (File.Exists(zipPath)) File.Delete(zipPath);
            CreateDeterministicZip(workspace, zipPath);
            if (new FileInfo(zipPath).Length > _options.MaximumPackageBytes)
            {
                throw new StateConflictException("The portable ZIP exceeds the configured package size limit.");
            }
            var zipDraft = await DraftAsync(
                zipPath, "lesson.zip", "application/zip", "portableZip", null, true, cancellationToken);
            var zipFile = await PersistStandaloneFileAsync(
                package, zipDraft, $"offline/{package.Id:N}/lesson.zip", cancellationToken);
            await EnsureLeaseAsync(package, leaseId);
            package.MarkReady(
                manifestFile.Id,
                zipFile.Id,
                totalBytes,
                zipFile.Sha256,
                snapshot.Title,
                timeProvider.GetUtcNow());
            await database.SaveChangesAsync(cancellationToken);
            File.Delete(zipPath);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            await HandleFailureAsync(
                package,
                leaseId,
                new ProcessorUnavailableException("Offline package lease heartbeat failed."));
        }
        catch (Exception exception)
        {
            await HandleFailureAsync(package, leaseId, exception);
        }
        finally
        {
            executionCancellation.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove offline workspace {Workspace}", workspace);
            }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch (IOException exception) { logger.LogWarning(exception, "Could not remove offline ZIP {ZipPath}", zipPath); }
        }
    }

    private async Task<(OfflinePackage Package, Guid LeaseId)?> TryClaimAsync(Guid packageId)
    {
        await using var transaction = await database.Database.BeginTransactionAsync();
        var package = await database.OfflinePackages
            .FromSqlInterpolated($"SELECT * FROM offline_packages WHERE id = {packageId} FOR UPDATE")
            .SingleOrDefaultAsync()
            ?? throw new ResourceNotFoundException("Offline package", packageId);
        var now = timeProvider.GetUtcNow();
        if (package.Status is OfflinePackageStatus.Ready or OfflinePackageStatus.Stale ||
            package.Attempts >= 3 ||
            package.Status == OfflinePackageStatus.Building && package.LeaseExpiresAt > now)
        {
            await transaction.CommitAsync();
            return null;
        }
        if (package.Status == OfflinePackageStatus.Building)
        {
            package.Fail("LEASE_EXPIRED", "The previous package build lease expired.", now);
            package.Retry();
        }
        var leaseId = Guid.CreateVersion7();
        package.MarkBuilding(now, leaseId, now.AddMinutes(30));
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return (package, leaseId);
    }

    private async Task RunHeartbeatAsync(
        Guid packageId,
        Guid leaseId,
        CancellationTokenSource executionCancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(executionCancellation.Token))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var heartbeatDatabase = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
                var expiresAt = timeProvider.GetUtcNow().AddMinutes(30);
                var updated = await heartbeatDatabase.OfflinePackages
                    .Where(item => item.Id == packageId && item.Status == OfflinePackageStatus.Building &&
                                   item.ExecutionLeaseId == leaseId)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(item => item.LeaseExpiresAt, expiresAt),
                        executionCancellation.Token);
                if (updated == 0)
                {
                    executionCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Offline package heartbeat failed for {OfflinePackageId}", packageId);
            executionCancellation.Cancel();
        }
    }

    private async Task EnsureLeaseAsync(OfflinePackage package, Guid leaseId)
    {
        await database.Entry(package).ReloadAsync();
        if (package.Status != OfflinePackageStatus.Building || package.ExecutionLeaseId != leaseId)
        {
            throw new StateConflictException("Offline package lease is no longer owned by this worker.");
        }
    }

    private async Task HandleFailureAsync(OfflinePackage package, Guid leaseId, Exception exception)
    {
        logger.LogError(exception, "Offline package {OfflinePackageId} failed", package.Id);
        await database.Entry(package).ReloadAsync();
        if (package.Status != OfflinePackageStatus.Building || package.ExecutionLeaseId != leaseId) return;
        var transient = IsTransient(exception);
        var code = exception is AccessLearnException accessLearn ? accessLearn.Code : "OFFLINE_PACKAGE_FAILED";
        var message = exception is AccessLearnException ? exception.Message : "Offline package generation failed.";
        package.Fail(code, message, timeProvider.GetUtcNow());
        await database.SaveChangesAsync(CancellationToken.None);
        if (transient && package.Attempts < 3)
        {
            package.Retry();
            await database.SaveChangesAsync(CancellationToken.None);
            try
            {
                var delays = new[] { 10, 60 };
                package.SetExternalJobId(scheduler.Schedule(
                    package.Id,
                    TimeSpan.FromSeconds(delays[Math.Min(package.Attempts - 1, delays.Length - 1)])));
                await database.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception scheduleException)
            {
                logger.LogWarning(
                    scheduleException,
                    "Offline package {OfflinePackageId} remains queued for reconciliation",
                    package.Id);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        StorageUnavailableException or ProcessorUnavailableException or IOException or TimeoutException => true,
        NpgsqlException npgsql when npgsql.IsTransient => true,
        PostgresException postgres when postgres.SqlState is "40001" or "40P01" or "53300" or "53200" => true,
        DbUpdateException { InnerException: not null } update => IsTransient(update.InnerException),
        _ => false
    };

    private async Task<PackageAssetDraft> CopySourceAsync(
        SourceAsset source,
        string workspace,
        string relativePath,
        string role,
        string? language,
        bool required,
        CancellationToken cancellationToken)
    {
        var localPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await using (var stored = await storage.OpenReadAsync(source.ObjectKey, cancellationToken))
        await using (var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            var buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await stored.Content.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                copied += read;
                var maximum = source.ByteLength > 0 ? source.ByteLength : _options.MaximumPackageBytes;
                if (copied > maximum)
                {
                    throw new FileContentInvalidException("A publication resource exceeded its pinned byte length.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        var draft = await DraftAsync(
            localPath, relativePath, source.ContentType, role, language, required, cancellationToken);
        if (source.ByteLength > 0 && source.ByteLength != draft.ByteLength ||
            !string.IsNullOrWhiteSpace(source.Sha256) && source.Sha256 != draft.Sha256)
        {
            throw new FileContentInvalidException("A publication resource failed offline package integrity validation.");
        }
        return draft;
    }

    private async Task<PackageAssetDraft> CopyDocumentAsync(
        SourceAsset source,
        string workspace,
        string relativePath,
        string language,
        string displayName,
        CancellationToken cancellationToken)
    {
        var draft = await CopySourceAsync(
            source,
            workspace,
            relativePath,
            "documentHtml",
            language,
            required: false,
            cancellationToken);
        var html = await File.ReadAllTextAsync(draft.LocalPath, Encoding.UTF8, cancellationToken);
        var head = Regex.Match(
            html,
            "<head(?:\\s[^>]*)?>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        if (!head.Success)
        {
            throw new FileContentInvalidException("Validated document HTML does not contain a head element.");
        }
        const string csp = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; base-uri 'none'; form-action 'none'\">";
        html = html.Insert(head.Index + head.Length, csp);
        await File.WriteAllTextAsync(draft.LocalPath, html, new UTF8Encoding(false), cancellationToken);
        var hardened = await DraftAsync(
            draft.LocalPath,
            draft.RelativePath,
            draft.ContentType,
            draft.Role,
            draft.Language,
            draft.IsRequired,
            cancellationToken);
        return hardened with { DisplayName = displayName };
    }

    private static async Task<PackageAssetDraft> DraftAsync(
        string localPath,
        string relativePath,
        string contentType,
        string role,
        string? language,
        bool required,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        return new PackageAssetDraft(
            localPath,
            relativePath.Replace('\\', '/'),
            contentType,
            length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            role,
            language,
            required);
    }

    private async Task<InternalAsset> PersistPackageAssetAsync(
        OfflinePackage package,
        PackageAssetDraft draft,
        int sequence,
        CancellationToken cancellationToken)
    {
        var existing = await (
                from persistedAsset in database.OfflinePackageAssets
                join persistedFile in database.StoredFiles on persistedAsset.StoredFileId equals persistedFile.Id
                where persistedAsset.OfflinePackageId == package.Id && persistedAsset.Path == draft.RelativePath
                select new { Asset = persistedAsset, File = persistedFile })
            .SingleOrDefaultAsync(cancellationToken);
        StoredFile file;
        OfflinePackageAsset packageAsset;
        if (existing is null)
        {
            var objectKey = $"offline/{package.Id:N}/content/{draft.RelativePath}";
            file = new StoredFile(objectKey, Path.GetFileName(draft.RelativePath), draft.ContentType, draft.ByteLength, draft.Sha256);
            packageAsset = new OfflinePackageAsset(
                package.Id,
                file.Id,
                sequence,
                draft.RelativePath,
                draft.Role,
                draft.Language,
                draft.IsRequired,
                draft.DisplayName);
            database.StoredFiles.Add(file);
            database.OfflinePackageAssets.Add(packageAsset);
            await database.SaveChangesAsync(cancellationToken);
        }
        else
        {
            file = existing.File;
            packageAsset = existing.Asset;
            if (file.Status is StoredFileStatus.Deleting or StoredFileStatus.Deleted)
            {
                throw new StateConflictException("Offline package asset cleanup already claimed this object.");
            }
            if (file.Sha256 != draft.Sha256 || file.ByteLength != draft.ByteLength)
            {
                throw new StateConflictException("Offline retry asset does not match persisted package metadata.");
            }
            if (file.Status == StoredFileStatus.Ready)
            {
                return new InternalAsset(
                    packageAsset.Sequence,
                    packageAsset.Path,
                    file.ObjectKey,
                    file.ContentType,
                    file.ByteLength,
                    file.Sha256,
                    packageAsset.Role,
                    packageAsset.Language,
                    packageAsset.IsRequired,
                    packageAsset.DisplayName);
            }
            if (file.Status == StoredFileStatus.Rejected) file.RetryQuarantine();
        }

        try
        {
            await using var content = new FileStream(
                draft.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var write = await storage.PutAsync(
                file.ObjectKey,
                content,
                file.ContentType,
                file.ByteLength,
                new Dictionary<string, string> { ["sha256"] = file.Sha256, ["offline-package-id"] = package.Id.ToString("N") },
                cancellationToken);
            file.MarkReady(write.ETag);
            await database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            file.Reject("Offline package asset upload failed.");
            await database.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        return new InternalAsset(
            packageAsset.Sequence,
            packageAsset.Path,
            file.ObjectKey,
            file.ContentType,
            file.ByteLength,
            file.Sha256,
            packageAsset.Role,
            packageAsset.Language,
            packageAsset.IsRequired,
            packageAsset.DisplayName);
    }

    private async Task<StoredFile> PersistStandaloneFileAsync(
        OfflinePackage package,
        PackageAssetDraft draft,
        string objectKey,
        CancellationToken cancellationToken)
    {
        var existing = await database.StoredFiles.SingleOrDefaultAsync(
            file => file.ObjectKey == objectKey,
            cancellationToken);
        StoredFile file;
        if (existing is not null)
        {
            if (existing.Status is StoredFileStatus.Deleting or StoredFileStatus.Deleted)
            {
                throw new StateConflictException("Offline package output cleanup already claimed this object.");
            }
            if (existing.Status == StoredFileStatus.Ready && existing.Sha256 == draft.Sha256) return existing;
            if (existing.Sha256 != draft.Sha256 || existing.ByteLength != draft.ByteLength)
            {
                if (existing.Status == StoredFileStatus.Ready)
                {
                    throw new StateConflictException("Ready offline output differs from regenerated content.");
                }
                existing.RefreshQuarantineMetadata(draft.ContentType, draft.ByteLength, draft.Sha256);
            }
            if (existing.Status == StoredFileStatus.Rejected) existing.RetryQuarantine();
            file = existing;
        }
        else
        {
            file = new StoredFile(objectKey, draft.RelativePath, draft.ContentType, draft.ByteLength, draft.Sha256);
            database.StoredFiles.Add(file);
            await database.SaveChangesAsync(cancellationToken);
        }
        await using var content = new FileStream(draft.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var write = await storage.PutAsync(
            objectKey,
            content,
            draft.ContentType,
            draft.ByteLength,
            new Dictionary<string, string> { ["sha256"] = draft.Sha256, ["offline-package-id"] = package.Id.ToString("N") },
            cancellationToken);
        file.MarkReady(write.ETag);
        await database.SaveChangesAsync(cancellationToken);
        return file;
    }

    private async Task<PublicationSnapshot> LoadPublicationSnapshotAsync(
        StoredFile manifestFile,
        LessonPublication publication,
        CancellationToken cancellationToken)
    {
        await using var stored = await storage.OpenReadAsync(manifestFile.ObjectKey, cancellationToken);
        using var output = new MemoryStream();
        await stored.Content.CopyToAsync(output, cancellationToken);
        var bytes = output.ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.Length > 2_097_152 || checksum != publication.Checksum || checksum != manifestFile.Sha256)
        {
            throw new FileContentInvalidException("The publication snapshot is invalid.");
        }
        var snapshot = JsonSerializer.Deserialize<PublicationSnapshot>(bytes, JsonOptions)
            ?? throw new FileContentInvalidException("The publication snapshot could not be read.");
        if (snapshot.LessonId != publication.LessonId || snapshot.PublicationId != publication.Id ||
            snapshot.Revision != publication.Revision)
        {
            throw new FileContentInvalidException("The publication snapshot identity is invalid.");
        }
        if (snapshot.SchemaVersion != "1.1" || snapshot.Documents is null ||
            snapshot.Primary.ByteLength <= 0 || string.IsNullOrWhiteSpace(snapshot.Primary.Sha256) ||
            snapshot.Captions.Any(caption => caption.ByteLength <= 0 || string.IsNullOrWhiteSpace(caption.Sha256)))
        {
            throw new StateConflictException(
                "This publication predates immutable offline metadata. Republish the lesson before generating a package.");
        }
        return snapshot;
    }

    private static string BuildPortableHtml(
        string title,
        PublicationSnapshot snapshot,
        IReadOnlyList<PackageAssetDraft> assets,
        IReadOnlyList<SnapshotDocument> documents)
    {
        var encoder = HtmlEncoder.Default;
        var audio = assets.Single(asset => asset.Role == "offlineAudio").RelativePath;
        var isAmharic = snapshot.SourceLanguage == "am";
        var transcriptHeading = isAmharic ? "የንግግር ጽሑፎች" : "Transcripts";
        var documentHeading = isAmharic ? "ሰነዶች" : "Documents";
        var captionHeading = isAmharic ? "ጽሑፍ መግለጫዎች" : "Captions";
        var output = new StringBuilder("<!doctype html><html lang=\"")
            .Append(encoder.Encode(snapshot.SourceLanguage))
            .Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(encoder.Encode(title)).Append("</title><link rel=\"stylesheet\" href=\"styles.css\"></head><body><main>")
            .Append("<h1>").Append(encoder.Encode(title)).Append("</h1><audio controls preload=\"metadata\" src=\"")
            .Append(encoder.Encode(audio)).Append("\">");
        foreach (var caption in assets.Where(asset => asset.Role == "caption"))
        {
            output.Append("<track kind=\"captions\" srclang=\"").Append(encoder.Encode(caption.Language ?? "und"))
                .Append("\" src=\"").Append(encoder.Encode(caption.RelativePath)).Append("\">");
        }
        output.Append("</audio><label>").Append(captionHeading)
            .Append("<select id=\"offline-caption-select\"><option value=\"\">Off</option>");
        foreach (var caption in assets.Where(asset => asset.Role == "caption"))
        {
            output.Append("<option value=\"").Append(encoder.Encode(caption.Language ?? "und")).Append("\">")
                .Append(encoder.Encode(caption.Language ?? "und")).Append("</option>");
        }
        output.Append("</select></label><p id=\"offline-caption-cue\" hidden></p><section><h2>")
            .Append(captionHeading).Append("</h2><ul>");
        foreach (var caption in assets.Where(asset => asset.Role == "caption"))
        {
            output.Append("<li><a href=\"").Append(encoder.Encode(caption.RelativePath)).Append("\">")
                .Append(encoder.Encode(caption.Language ?? "und")).Append(" WebVTT</a></li>");
        }
        output.Append("</ul></section><section><h2>").Append(transcriptHeading).Append("</h2>");
        foreach (var caption in snapshot.Captions)
        {
            output.Append("<article lang=\"").Append(encoder.Encode(caption.Language)).Append("\"><h3>")
                .Append(encoder.Encode(caption.Language)).Append("</h3>");
            foreach (var segment in caption.Segments)
            {
                output.Append("<p>").Append(encoder.Encode(
                    segment.SpeakerLabel is null ? segment.Text : $"{segment.SpeakerLabel}: {segment.Text}"))
                    .Append("</p>");
            }
            output.Append("</article>");
        }
        output.Append("</section>");
        if (documents.Count > 0)
        {
            output.Append("<section><h2>").Append(documentHeading).Append("</h2><ul>");
            foreach (var document in documents)
            {
                output.Append("<li><a href=\"documents/").Append(document.DocumentId.ToString("N"))
                    .Append('-').Append(document.SourceRevision).Append(".html\">")
                    .Append(encoder.Encode(document.Title)).Append("</a></li>");
            }
            output.Append("</ul></section>");
        }
        output.Append("</main><script>")
            .Append("const a=document.querySelector('audio'),s=document.getElementById('offline-caption-select'),c=document.getElementById('offline-caption-cue');")
            .Append("s.onchange=()=>{for(const t of a.textTracks){t.mode=s.value&&t.language===s.value?'hidden':'disabled';t.oncuechange=()=>{const v=t.activeCues?Array.from(t.activeCues).map(x=>x.getCueAsHTML?x.getCueAsHTML().textContent:(x.text||'')):[];c.textContent=v.join('\\n');c.hidden=!v.length}}};")
            .Append("</script></body></html>");
        return output.ToString();
    }

    private static void RestrictWorkspacePermissions(string workspace)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                workspace,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static void CreateDeterministicZip(string sourceDirectory, string zipPath)
    {
        using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        var timestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = timestamp;
            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var entryStream = entry.Open();
            input.CopyTo(entryStream);
        }
    }

    private const string PortableStyles = """
        :root{font-family:system-ui,"Noto Sans Ethiopic",sans-serif;color:#171717;background:#fff;line-height:1.55}
        body{margin:0}main{width:min(52rem,calc(100% - 2rem));margin:1rem auto}audio{width:100%;margin:1rem 0}select{min-height:2.75rem;margin:.5rem}
        #offline-caption-cue{font-size:1.25rem;background:#111;color:#fff;padding:.5rem;white-space:pre-wrap}
        article{border-top:1px solid #777;padding:.5rem 0}a{color:#0645ad}a:focus-visible,a:hover{text-decoration-thickness:.2em}
        @media(prefers-color-scheme:dark){:root{color:#f5f5f5;background:#151515}a{color:#8ec7ff}}
        """;

    private sealed record SourceAsset(string ObjectKey, string ContentType, long ByteLength, string Sha256);
    private sealed record PackageAssetDraft(
        string LocalPath,
        string RelativePath,
        string ContentType,
        long ByteLength,
        string Sha256,
        string Role,
        string? Language,
        bool IsRequired,
        string? DisplayName = null);
    private sealed record InternalAsset(
        int Sequence,
        string Path,
        string ObjectKey,
        string ContentType,
        long ByteLength,
        string Sha256,
        string Role,
        string? Language,
        bool IsRequired,
        string? DisplayName);
    private sealed record PortableManifest(
        string SchemaVersion,
        Guid PackageId,
        Guid LessonId,
        Guid PublicationId,
        int PublicationRevision,
        string Title,
        string Profile,
        long TotalBytes,
        IReadOnlyList<PortableAsset> Assets);
    private sealed record PortableAsset(
        int Sequence,
        string Path,
        string ContentType,
        long ByteLength,
        string Sha256,
        string Role,
        string? Language,
        bool IsRequired,
        string? DisplayName);
    private sealed record PublicationSnapshot(
        string SchemaVersion,
        Guid LessonId,
        Guid PublicationId,
        int Revision,
        long LessonDraftRevision,
        string Title,
        string SourceLanguage,
        SnapshotAsset Primary,
        SnapshotAsset? Poster,
        IReadOnlyList<SnapshotCaption> Captions,
        SnapshotAsset? SignLanguage,
        SnapshotAsset? AudioDescription,
        SnapshotAsset? OfflineAudio,
        IReadOnlyList<SnapshotDocument>? Documents,
        IReadOnlyList<string> Warnings);
    private sealed record SnapshotAsset(
        Guid AssetId,
        Guid FileId,
        string ObjectKey,
        string ContentType,
        long ByteLength,
        string Sha256,
        MediaAssetRole Role,
        long Version,
        Guid? SourceMediaAssetId,
        long? SourceMediaVersion,
        string? Language,
        long? DurationMilliseconds,
        long SyncOffsetMilliseconds,
        bool IsHumanReviewed,
        string? AccessibilityMode);
    private sealed record SnapshotCaption(
        Guid TranscriptId,
        string Language,
        int Revision,
        Guid FileId,
        string ObjectKey,
        long ByteLength,
        string Sha256,
        IReadOnlyList<SnapshotSegment> Segments);
    private sealed record SnapshotDocument(
        Guid DocumentId,
        Guid OutputId,
        long SourceRevision,
        string Title,
        string Language,
        Guid FileId,
        string ObjectKey,
        string ContentType,
        long ByteLength,
        string Sha256);
    private sealed record SnapshotSegment(
        int Sequence,
        long StartMilliseconds,
        long EndMilliseconds,
        string Text,
        string? SpeakerLabel);
}
