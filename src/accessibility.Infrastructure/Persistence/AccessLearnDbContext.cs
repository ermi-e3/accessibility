using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Infrastructure.Persistence;

public sealed class AccessLearnDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private readonly TimeProvider _timeProvider;

    public AccessLearnDbContext(DbContextOptions<AccessLearnDbContext> options, TimeProvider timeProvider)
        : base(options)
    {
        _timeProvider = timeProvider;
    }

    public DbSet<Lesson> Lessons => Set<Lesson>();

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    public DbSet<Transcript> Transcripts => Set<Transcript>();

    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();

    public DbSet<CaptionArtifact> CaptionArtifacts => Set<CaptionArtifact>();

    public DbSet<AccessibleDocument> Documents => Set<AccessibleDocument>();

    public DbSet<DocumentImage> DocumentImages => Set<DocumentImage>();

    public DbSet<DocumentOutput> DocumentOutputs => Set<DocumentOutput>();

    public DbSet<LessonPublication> LessonPublications => Set<LessonPublication>();

    public DbSet<OfflinePackage> OfflinePackages => Set<OfflinePackage>();

    public DbSet<OfflinePackageAsset> OfflinePackageAssets => Set<OfflinePackageAsset>();

    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();

    public DbSet<ProcessingJobAttempt> ProcessingJobAttempts => Set<ProcessingJobAttempt>();

    public DbSet<ProcessingIdempotencyRequest> ProcessingIdempotencyRequests => Set<ProcessingIdempotencyRequest>();

    public DbSet<IntegrationClient> IntegrationClients => Set<IntegrationClient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccessLearnDbContext).Assembly);
        SnakeCaseNaming.Apply(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        MarkChangedTranscriptParents();
        ApplyAuditValues();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        await MarkChangedTranscriptParentsAsync(cancellationToken);
        ApplyAuditValues();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void MarkChangedTranscriptParents()
    {
        foreach (var transcriptId in ChangedTranscriptIds())
        {
            var transcriptEntry = ChangeTracker.Entries<Transcript>()
                .SingleOrDefault(entry => entry.Entity.Id == transcriptId);
            transcriptEntry ??= Entry(Transcripts.Single(transcript => transcript.Id == transcriptId));
            MarkTranscriptModified(transcriptEntry);
        }
    }

    private async Task MarkChangedTranscriptParentsAsync(CancellationToken cancellationToken)
    {
        foreach (var transcriptId in ChangedTranscriptIds())
        {
            var transcriptEntry = ChangeTracker.Entries<Transcript>()
                .SingleOrDefault(entry => entry.Entity.Id == transcriptId);
            transcriptEntry ??= Entry(await Transcripts.SingleAsync(
                transcript => transcript.Id == transcriptId,
                cancellationToken));
            MarkTranscriptModified(transcriptEntry);
        }
    }

    private HashSet<Guid> ChangedTranscriptIds()
    {
        return ChangeTracker.Entries<TranscriptSegment>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.TranscriptId)
            .ToHashSet();
    }

    private static void MarkTranscriptModified(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Transcript> entry)
    {
        if (entry.State == EntityState.Unchanged)
        {
            entry.Property(transcript => transcript.UpdatedAt).IsModified = true;
        }
    }

    private void ApplyAuditValues()
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                entry.Entity.Version = 1;
                continue;
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            entry.Property(entity => entity.CreatedAt).IsModified = false;
            entry.Entity.UpdatedAt = now;
            entry.Entity.Version++;
        }
    }
}
