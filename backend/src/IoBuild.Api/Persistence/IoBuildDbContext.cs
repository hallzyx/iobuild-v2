using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Persistence;

public sealed class IoBuildDbContext(DbContextOptions<IoBuildDbContext> options) : DbContext(options)
{
    public DbSet<FoundationRecord> FoundationRecords => Set<FoundationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FoundationRecord>(entity =>
        {
            entity.ToTable("foundation_records");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Name).HasMaxLength(200).IsRequired();
        });
    }
}

public sealed class FoundationRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
