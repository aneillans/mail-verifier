using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Models;

namespace MailVerifier.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<VerificationJob> VerificationJobs => Set<VerificationJob>();
    public DbSet<VerificationResult> VerificationResults => Set<VerificationResult>();
    public DbSet<JobEmail> JobEmails => Set<JobEmail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<VerificationJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UploadedByUser).IsRequired();
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
        });

        modelBuilder.Entity<JobEmail>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmailAddress).IsRequired();
        });
    }
}
