using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Models;

namespace MailVerifier.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<VerificationJob> VerificationJobs => Set<VerificationJob>();
    public DbSet<VerificationResult> VerificationResults => Set<VerificationResult>();
    public DbSet<JobEmail> JobEmails => Set<JobEmail>();
    public DbSet<SoftFailureRecipient> SoftFailureRecipients => Set<SoftFailureRecipient>();
    public DbSet<SoftFailureEvent> SoftFailureEvents => Set<SoftFailureEvent>();
    public DbSet<SoftFailureUploadBatch> SoftFailureUploadBatches => Set<SoftFailureUploadBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<VerificationJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UploadedByUser).IsRequired();
            entity.Property(e => e.UploadedByName);
            entity.Property(e => e.Status).IsRequired();
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.UploadedByUser, e.CreatedAt });
            entity.HasMany(e => e.Results)
                  .WithOne(r => r.Job)
                  .HasForeignKey(r => r.JobId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.JobEmails)
                  .WithOne(e => e.Job)
                  .HasForeignKey(e => e.JobId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VerificationResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmailAddress).IsRequired();
            entity.HasIndex(e => new { e.JobId, e.EmailAddress }).IsUnique();
            entity.HasIndex(e => e.EmailAddress);
        });

        modelBuilder.Entity<JobEmail>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmailAddress).IsRequired();
            entity.HasIndex(e => new { e.JobId, e.EmailAddress });
        });

        modelBuilder.Entity<SoftFailureRecipient>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmailAddress).IsRequired().HasMaxLength(320);
            entity.HasIndex(e => e.EmailAddress).IsUnique();
            entity.HasIndex(e => e.LastSeenAt);
            entity.HasMany(e => e.Events)
                  .WithOne(e => e.Recipient)
                  .HasForeignKey(e => e.RecipientId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SoftFailureUploadBatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).HasMaxLength(256);
            entity.Property(e => e.UploadedByUser).IsRequired();
            entity.Property(e => e.UploadedByName);
            entity.HasIndex(e => e.UploadedAt);
            entity.HasMany(e => e.Events)
                  .WithOne(e => e.UploadBatch)
                  .HasForeignKey(e => e.UploadBatchId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SoftFailureEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ErrorCode).HasMaxLength(128);
            entity.Property(e => e.Response).HasMaxLength(2048);
            entity.HasIndex(e => e.RecipientId);
            entity.HasIndex(e => e.UploadBatchId);
            entity.HasIndex(e => e.RecordedAt);
            entity.HasIndex(e => new { e.RecipientId, e.RecordedAt });
        });
    }
}
