using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Accessibility.Infrastructure.Persistence.Configurations;

internal static class ConfigurationDefaults
{
    public static void ConfigureEntity<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : Entity
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
    }

    public static void ConfigureAuditableEntity<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableEntity
    {
        ConfigureEntity(builder);
        builder.Property(entity => entity.CreatedAt).IsRequired();
        builder.Property(entity => entity.UpdatedAt).IsRequired();
        builder.Property(entity => entity.Version).IsConcurrencyToken().IsRequired();
    }
}

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.IsActive).IsRequired();
        builder.HasIndex(user => user.NormalizedEmail)
            .HasDatabaseName("EmailIndex")
            .IsUnique();
    }
}

internal sealed class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(lesson => lesson.Title).HasMaxLength(200).IsRequired();
        builder.Property(lesson => lesson.Description).HasMaxLength(2_000);
        builder.Property(lesson => lesson.SourceLanguage).HasMaxLength(35).IsRequired();
        builder.Property(lesson => lesson.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(lesson => lesson.DraftRevision).IsRequired();

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(lesson => lesson.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<LessonPublication>()
            .WithMany()
            .HasForeignKey(nameof(Lesson.CurrentPublicationId), nameof(Lesson.Id))
            .HasPrincipalKey(nameof(LessonPublication.Id), nameof(LessonPublication.LessonId))
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(lesson => new { lesson.OwnerUserId, lesson.UpdatedAt });
        builder.HasIndex(lesson => lesson.Status);
    }
}

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(file => file.ObjectKey).HasMaxLength(1_024).IsRequired();
        builder.Property(file => file.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(file => file.ContentType).HasMaxLength(255).IsRequired();
        builder.Property(file => file.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(file => file.ETag).HasMaxLength(255);
        builder.Property(file => file.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(file => file.RejectionReason).HasMaxLength(1_000);

        builder.HasIndex(file => file.ObjectKey).IsUnique();
        builder.HasIndex(file => new { file.Sha256, file.ByteLength });
        builder.ToTable(table => table.HasCheckConstraint("ck_stored_files_byte_length", "byte_length > 0"));
    }
}

internal sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(asset => asset.Role).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(asset => asset.Language).HasMaxLength(35);
        builder.Property(asset => asset.Codec).HasMaxLength(100);
        builder.Property(asset => asset.AccessibilityMode).HasMaxLength(32);
        builder.Property(asset => asset.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne<Lesson>()
            .WithMany()
            .HasForeignKey(asset => asset.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(asset => asset.StoredFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(asset => asset.SourceMediaAssetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProcessingJob>()
            .WithMany()
            .HasForeignKey(asset => asset.ProcessingJobId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(asset => asset.LessonId)
            .IsUnique()
            .HasFilter("is_active = TRUE AND role IN ('PrimaryVideo', 'PrimaryAudio')");
        builder.HasIndex(asset => new { asset.LessonId, asset.Role })
            .IsUnique()
            .HasFilter(
                "is_active = TRUE AND role IN ('SignLanguage', 'AudioDescription', 'OfflineAudio', 'Poster')");
        builder.HasIndex(asset => asset.StoredFileId).IsUnique();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_media_assets_duration",
                "duration_milliseconds IS NULL OR duration_milliseconds > 0");
            table.HasCheckConstraint(
                "ck_media_assets_sync_offset",
                "sync_offset_milliseconds >= 0");
            table.HasCheckConstraint(
                "ck_media_assets_dimensions",
                "(width IS NULL OR width > 0) AND (height IS NULL OR height > 0)");
            table.HasCheckConstraint(
                "ck_media_assets_sign_language",
                "role <> 'SignLanguage' OR language IS NOT NULL AND language = 'eth'");
            table.HasCheckConstraint(
                "ck_media_assets_accessibility_review",
                "role NOT IN ('SignLanguage', 'AudioDescription') OR is_active = FALSE OR " +
                "is_human_reviewed = TRUE AND " +
                "((role = 'SignLanguage' AND accessibility_mode = 'human-recorded') OR " +
                "(role = 'AudioDescription' AND accessibility_mode = 'full-mix'))");
            table.HasCheckConstraint(
                "ck_media_assets_source_provenance",
                "(source_media_asset_id IS NULL AND source_media_version IS NULL) OR " +
                "(source_media_asset_id IS NOT NULL AND source_media_version > 0)");
        });
    }
}

internal sealed class TranscriptConfiguration : IEntityTypeConfiguration<Transcript>
{
    public void Configure(EntityTypeBuilder<Transcript> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(transcript => transcript.Language).HasMaxLength(35).IsRequired();
        builder.Property(transcript => transcript.Origin).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(transcript => transcript.ReviewStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(transcript => transcript.Provider).HasMaxLength(100);
        builder.Property(transcript => transcript.ProviderVersion).HasMaxLength(100);

        builder.HasOne<Lesson>()
            .WithMany()
            .HasForeignKey(transcript => transcript.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Transcript>()
            .WithMany()
            .HasForeignKey(transcript => transcript.SourceTranscriptId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Transcript>()
            .WithMany()
            .HasForeignKey(transcript => transcript.RevisionOfTranscriptId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(transcript => transcript.SourceMediaAssetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transcript => transcript.ApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProcessingJob>()
            .WithMany()
            .HasForeignKey(transcript => transcript.ProcessingJobId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(transcript => new { transcript.LessonId, transcript.Language, transcript.Revision }).IsUnique();
        builder.HasIndex(transcript => new { transcript.ProcessingJobId, transcript.Language })
            .IsUnique()
            .HasFilter("processing_job_id IS NOT NULL");
        builder.HasIndex(transcript => new { transcript.LessonId, transcript.Language, transcript.ReviewStatus })
            .IsUnique()
            .HasFilter("review_status = 'Approved'");
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_transcripts_revision", "revision > 0 AND source_lesson_revision > 0");
            table.HasCheckConstraint(
                "ck_transcripts_source_media",
                "(source_media_asset_id IS NULL AND source_media_version IS NULL) OR " +
                "(source_media_asset_id IS NOT NULL AND source_media_version > 0)");
            table.HasCheckConstraint(
                "ck_transcripts_generated_provenance",
                "origin <> 'Generated' OR processing_job_id IS NOT NULL AND provider IS NOT NULL");
        });
    }
}

internal sealed class TranscriptSegmentConfiguration : IEntityTypeConfiguration<TranscriptSegment>
{
    public void Configure(EntityTypeBuilder<TranscriptSegment> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(segment => segment.Text).HasMaxLength(4_000).IsRequired();
        builder.Property(segment => segment.SpeakerLabel).HasMaxLength(100);

        builder.HasOne<Transcript>()
            .WithMany()
            .HasForeignKey(segment => segment.TranscriptId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(segment => new { segment.TranscriptId, segment.Sequence }).IsUnique();
        builder.HasIndex(segment => new { segment.TranscriptId, segment.StartMilliseconds });
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_transcript_segments_timing",
            "sequence >= 0 AND start_milliseconds >= 0 AND end_milliseconds > start_milliseconds"));
    }
}

internal sealed class CaptionArtifactConfiguration : IEntityTypeConfiguration<CaptionArtifact>
{
    public void Configure(EntityTypeBuilder<CaptionArtifact> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(artifact => artifact.Format).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(artifact => artifact.GeneratorVersion).HasMaxLength(100).IsRequired();
        builder.Property(artifact => artifact.Checksum).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(artifact => artifact.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne<Transcript>()
            .WithMany()
            .HasForeignKey(artifact => artifact.TranscriptId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(artifact => artifact.StoredFileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(artifact => new
        {
            artifact.TranscriptId,
            artifact.Format,
            artifact.GeneratorVersion,
            artifact.Checksum
        }).IsUnique();
        builder.HasIndex(artifact => new { artifact.TranscriptId, artifact.Format, artifact.Status })
            .IsUnique()
            .HasFilter("status = 'Ready'");
        builder.HasIndex(artifact => artifact.StoredFileId).IsUnique();
    }
}

internal sealed class AccessibleDocumentConfiguration : IEntityTypeConfiguration<AccessibleDocument>
{
    public void Configure(EntityTypeBuilder<AccessibleDocument> builder)
    {
        builder.ToTable("documents");
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(document => document.Title).HasMaxLength(200).IsRequired();
        builder.Property(document => document.Language).HasMaxLength(35).IsRequired();
        builder.Property(document => document.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(document => document.NormalizerVersion).HasMaxLength(100);
        builder.Property(document => document.IssueSummaryJson).HasColumnType("jsonb");

        builder.HasOne<Lesson>()
            .WithMany()
            .HasForeignKey(document => document.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(document => document.SourceFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(document => document.NormalizedModelFileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(document => document.SourceFileId).IsUnique();
    }
}

internal sealed class DocumentImageConfiguration : IEntityTypeConfiguration<DocumentImage>
{
    public void Configure(EntityTypeBuilder<DocumentImage> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(image => image.AltText).HasMaxLength(2_000);
        builder.Property(image => image.RelationshipId).HasMaxLength(200).IsRequired();
        builder.Property(image => image.SourceLocation).HasMaxLength(500).IsRequired();
        builder.Property(image => image.SuggestedAltText).HasMaxLength(2_000);
        builder.Property(image => image.ReviewStatus).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne<AccessibleDocument>()
            .WithMany()
            .HasForeignKey(image => image.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(image => image.StoredFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(image => image.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(image => new { image.DocumentId, image.Sequence }).IsUnique();
        builder.HasIndex(image => new { image.DocumentId, image.RelationshipId });
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_document_images_approved_alternative",
            "review_status <> 'Approved' OR (reviewed_by_user_id IS NOT NULL AND reviewed_at IS NOT NULL AND ((is_decorative AND alt_text IS NULL) OR (NOT is_decorative AND alt_text IS NOT NULL AND length(btrim(alt_text)) > 0)))"));
    }
}

internal sealed class DocumentOutputConfiguration : IEntityTypeConfiguration<DocumentOutput>
{
    public void Configure(EntityTypeBuilder<DocumentOutput> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(output => output.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(output => output.GeneratorVersion).HasMaxLength(100).IsRequired();
        builder.Property(output => output.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(output => output.ValidatorName).HasMaxLength(100);
        builder.Property(output => output.ValidatorVersion).HasMaxLength(100);
        builder.Property(output => output.ValidationProfile).HasMaxLength(100);
        builder.Property(output => output.ValidationSummaryJson).HasColumnType("jsonb");

        builder.HasOne<AccessibleDocument>()
            .WithMany()
            .HasForeignKey(output => output.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(output => output.StoredFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(output => output.ValidationReportFileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(output => new { output.DocumentId, output.SourceRevision, output.Kind, output.GeneratorVersion }).IsUnique();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_document_outputs_source_revision", "source_revision > 0");
            table.HasCheckConstraint(
                "ck_document_outputs_validation_provenance",
                "status NOT IN ('Validated', 'ValidationFailed', 'Failed') OR (validation_report_file_id IS NOT NULL AND validator_name IS NOT NULL AND validator_version IS NOT NULL AND validation_profile IS NOT NULL AND validation_summary_json IS NOT NULL AND validated_at IS NOT NULL)");
        });
    }
}

internal sealed class LessonPublicationConfiguration : IEntityTypeConfiguration<LessonPublication>
{
    public void Configure(EntityTypeBuilder<LessonPublication> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(publication => publication.Checksum).HasMaxLength(64).IsFixedLength().IsRequired();

        builder.HasOne<Lesson>()
            .WithMany()
            .HasForeignKey(publication => publication.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(publication => publication.ManifestFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(publication => publication.PublishedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(publication => new { publication.LessonId, publication.Revision }).IsUnique();
        builder.HasAlternateKey(publication => new { publication.Id, publication.LessonId });
        builder.ToTable(table => table.HasCheckConstraint("ck_lesson_publications_revision", "revision > 0"));
    }
}

internal sealed class OfflinePackageConfiguration : IEntityTypeConfiguration<OfflinePackage>
{
    public void Configure(EntityTypeBuilder<OfflinePackage> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(package => package.Profile).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(package => package.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(package => package.Checksum).HasMaxLength(64).IsFixedLength();
        builder.Property(package => package.Error).HasMaxLength(2_000);
        builder.Property(package => package.ErrorCode).HasMaxLength(100);
        builder.Property(package => package.ExternalJobId).HasMaxLength(200);
        builder.Property(package => package.Title).HasMaxLength(200);

        builder.HasOne<LessonPublication>()
            .WithMany()
            .HasForeignKey(package => package.LessonPublicationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(package => package.ManifestFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(package => package.ZipFileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(package => new { package.LessonPublicationId, package.Profile }).IsUnique();
        builder.HasIndex(package => new { package.Status, package.LeaseExpiresAt });
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_offline_packages_total_bytes",
            "total_bytes IS NULL OR total_bytes > 0"));
    }
}

internal sealed class OfflinePackageAssetConfiguration : IEntityTypeConfiguration<OfflinePackageAsset>
{
    public void Configure(EntityTypeBuilder<OfflinePackageAsset> builder)
    {
        ConfigurationDefaults.ConfigureEntity(builder);
        builder.Property(asset => asset.Path).HasMaxLength(512).IsRequired();
        builder.Property(asset => asset.Role).HasMaxLength(50).IsRequired();
        builder.Property(asset => asset.Language).HasMaxLength(35);
        builder.Property(asset => asset.DisplayName).HasMaxLength(200);
        builder.HasOne<OfflinePackage>()
            .WithMany()
            .HasForeignKey(asset => asset.OfflinePackageId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(asset => asset.StoredFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(asset => new { asset.OfflinePackageId, asset.Sequence }).IsUnique();
        builder.HasIndex(asset => new { asset.OfflinePackageId, asset.Path }).IsUnique();
        builder.HasIndex(asset => asset.StoredFileId).IsUnique();
    }
}

internal sealed class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(job => job.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(job => job.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(job => job.Provider).HasMaxLength(100);
        builder.Property(job => job.ExternalJobId).HasMaxLength(200);
        builder.Property(job => job.PayloadJson).HasColumnType("jsonb");
        builder.Property(job => job.ClientRequestKey).HasMaxLength(200);
        builder.Property(job => job.RequestFingerprint).HasMaxLength(200);
        builder.Property(job => job.ErrorCode).HasMaxLength(100);
        builder.Property(job => job.ErrorMessage).HasMaxLength(2_000);

        builder.HasOne<Lesson>()
            .WithMany()
            .HasForeignKey(job => job.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AccessibleDocument>()
            .WithMany()
            .HasForeignKey(job => job.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(job => job.MediaAssetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProcessingJob>()
            .WithMany()
            .HasForeignKey(job => job.ParentJobId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(job => job.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(job => job.IdempotencyKey).IsUnique();
        builder.HasIndex(job => new { job.RequestedByUserId, job.ClientRequestKey })
            .IsUnique()
            .HasFilter("client_request_key IS NOT NULL");
        builder.HasIndex(job => new { job.Status, job.CreatedAt });
        builder.HasIndex(job => new { job.Status, job.LeaseExpiresAt });
        builder.HasIndex(job => job.LessonId);
        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_processing_jobs_target",
                "lesson_id IS NOT NULL OR document_id IS NOT NULL OR media_asset_id IS NOT NULL");
            table.HasCheckConstraint(
                "ck_processing_jobs_progress",
                "progress_percentage >= 0 AND progress_percentage <= 100");
        });
    }
}

internal sealed class ProcessingJobAttemptConfiguration : IEntityTypeConfiguration<ProcessingJobAttempt>
{
    public void Configure(EntityTypeBuilder<ProcessingJobAttempt> builder)
    {
        ConfigurationDefaults.ConfigureEntity(builder);
        builder.Property(attempt => attempt.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(attempt => attempt.Provider).HasMaxLength(100);
        builder.Property(attempt => attempt.ErrorCode).HasMaxLength(100);
        builder.Property(attempt => attempt.ErrorMessage).HasMaxLength(2_000);
        builder.Property(attempt => attempt.Version).IsConcurrencyToken().IsRequired();
        builder.HasOne<ProcessingJob>()
            .WithMany()
            .HasForeignKey(attempt => attempt.ProcessingJobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(attempt => new { attempt.ProcessingJobId, attempt.AttemptNumber }).IsUnique();
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_processing_job_attempts_number",
            "attempt_number > 0"));
    }
}

internal sealed class ProcessingIdempotencyRequestConfiguration
    : IEntityTypeConfiguration<ProcessingIdempotencyRequest>
{
    public void Configure(EntityTypeBuilder<ProcessingIdempotencyRequest> builder)
    {
        ConfigurationDefaults.ConfigureEntity(builder);
        builder.Property(request => request.ClientKey).HasMaxLength(200).IsRequired();
        builder.Property(request => request.RequestFingerprint).HasMaxLength(200).IsRequired();
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(request => request.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ProcessingJob>()
            .WithMany()
            .HasForeignKey(request => request.ProcessingJobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(request => new { request.OwnerUserId, request.ClientKey }).IsUnique();
        builder.HasIndex(request => request.ProcessingJobId);
    }
}

internal sealed class IntegrationClientConfiguration : IEntityTypeConfiguration<IntegrationClient>
{
    public void Configure(EntityTypeBuilder<IntegrationClient> builder)
    {
        ConfigurationDefaults.ConfigureAuditableEntity(builder);
        builder.Property(client => client.Name).HasMaxLength(200).IsRequired();
        builder.Property(client => client.KeyPrefix).HasMaxLength(32).IsRequired();
        builder.Property(client => client.KeyHash).HasMaxLength(256).IsRequired();
        builder.Property(client => client.Scopes).HasColumnType("text[]").IsRequired();
        builder.Property(client => client.AllowedOrigins).HasColumnType("text[]").IsRequired();
        builder.Property(client => client.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(client => client.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(client => client.KeyPrefix).IsUnique();
        builder.HasIndex(client => client.OwnerUserId);
    }
}
