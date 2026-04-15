using Microsoft.EntityFrameworkCore;

namespace EasyCicd.Data;

public class DeploymentDbContext : DbContext
{
    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DeploymentDbContext(DbContextOptions<DeploymentDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Deployment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.RepoName).IsRequired();
            entity.Property(e => e.CommitSha).IsRequired();
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasDefaultValue(DeploymentStatus.Pending);
            entity.Property(e => e.Attempt).HasDefaultValue(1);
            entity.Property(e => e.CreatedAt).IsRequired();
        });
    }
}
