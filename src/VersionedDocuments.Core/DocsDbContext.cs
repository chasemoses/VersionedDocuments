using Microsoft.EntityFrameworkCore;

namespace VersionedDocuments.Core;

public class DocsDbContext(DbContextOptions<DocsDbContext> options) : DbContext(options)
{
    public DbSet<EquipmentItem> EquipmentItems => Set<EquipmentItem>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<EquipmentItem>(e =>
        {
            e.Property(x => x.Tag).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Tag).IsUnique();
        });

        b.Entity<Document>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.RemovedBy).HasMaxLength(100);
            e.Property(x => x.RemovedReason).HasMaxLength(500);
            e.Property(x => x.CurrentVersionNumber).IsConcurrencyToken();
            e.Ignore(x => x.IsRemoved);
            e.HasMany(x => x.Versions).WithOne(v => v.Document!).HasForeignKey(v => v.DocumentId);
        });

        b.Entity<DocumentVersion>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.UploadedBy).HasMaxLength(100);
            e.Property(x => x.Comment).HasMaxLength(500);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
            // The database, not just the code, guarantees version numbers never repeat.
            e.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
            // Purge asks "is this blob still referenced anywhere?"
            e.HasIndex(x => x.Sha256);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Actor).HasMaxLength(100);
            e.HasIndex(x => x.DocumentId);
        });
    }
}
