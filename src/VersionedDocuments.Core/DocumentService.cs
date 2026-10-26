using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace VersionedDocuments.Core;

/// <summary>An expected failure with a message safe to show the user.</summary>
public class DocumentException(string message) : Exception(message);

public sealed class ConcurrencyConflictException(string message) : DocumentException(message);

public sealed class DocumentOptions
{
    public long MaxBytes { get; set; } = 25 * 1024 * 1024;
    public string TempDirectory { get; set; } = Path.GetTempPath();
}

/// <summary>
/// All document operations. Each call uses its own DbContext from the factory, so the service
/// is safe to use from long-lived Blazor Server circuits and from background work alike.
/// </summary>
public sealed class DocumentService(
    IDbContextFactory<DocsDbContext> dbFactory,
    IBlobStore blobs,
    DocumentOptions options,
    TimeProvider clock)
{
    private sealed record StagedFile(string TempPath, string Sha256, long Size, ContentKind Kind);

    public async Task<Document> CreateAsync(int itemId, string title, string fileName, Stream content,
        string actor, string? comment = null, CancellationToken ct = default)
    {
        title = Required(title, "A title is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.EquipmentItems.AnyAsync(i => i.Id == itemId, ct))
            throw new DocumentException($"Equipment item {itemId} does not exist.");

        var staged = await StageAsync(content, fileName, ct);
        var now = clock.GetUtcNow();
        var doc = new Document
        {
            EquipmentItemId = itemId,
            Title = title,
            CreatedAt = now,
            CreatedBy = actor,
            CurrentVersionNumber = 1,
            Versions = [NewVersion(staged, 1, fileName, actor, comment, now)],
        };
        db.Documents.Add(doc);

        // Blob first, then the row. If the save fails we're left with an unreferenced blob,
        // which is harmless. The reverse order could leave a row pointing at missing content.
        blobs.Adopt(staged.Sha256, staged.TempPath);
        // The new id is only known after the first save, so the audit entry joins it in a transaction.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(db, doc.Id, AuditAction.Created, actor, $"v1 {doc.Versions[0].FileName}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc;
    }

    /// <param name="expectedCurrentVersion">
    /// The version the user was looking at. If someone else uploaded in the meantime the call
    /// fails instead of silently stacking a version on top of one the user never saw.
    /// </param>
    public async Task<DocumentVersion> AddVersionAsync(int documentId, int expectedCurrentVersion, string fileName,
        Stream content, string actor, string? comment = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await FindAsync(db, documentId, ct);
        if (doc.IsRemoved) throw new DocumentException("Restore this document before adding a version.");
        if (doc.CurrentVersionNumber != expectedCurrentVersion)
            throw Conflict(doc.CurrentVersionNumber);

        var staged = await StageAsync(content, fileName, ct);
        var version = NewVersion(staged, doc.CurrentVersionNumber + 1, fileName, actor, comment, clock.GetUtcNow());
        version.DocumentId = doc.Id;
        db.DocumentVersions.Add(version);
        doc.CurrentVersionNumber = version.VersionNumber;

        Audit(db, doc.Id, AuditAction.VersionAdded, actor, $"v{version.VersionNumber} {version.FileName}");
        blobs.Adopt(staged.Sha256, staged.TempPath);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another upload committed between our read and our save.
            throw Conflict(await CurrentVersionAsync(documentId, ct));
        }
        return version;
    }

    public async Task RemoveAsync(int documentId, string actor, string reason, CancellationToken ct = default)
    {
        reason = Required(reason, "A reason is required to remove a document.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await FindAsync(db, documentId, ct);
        if (doc.IsRemoved) return;
        doc.RemovedAt = clock.GetUtcNow();
        doc.RemovedBy = actor;
        doc.RemovedReason = reason;
        Audit(db, doc.Id, AuditAction.Removed, actor, reason);
        await db.SaveChangesAsync(ct);
    }

    public async Task RestoreAsync(int documentId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await FindAsync(db, documentId, ct);
        if (!doc.IsRemoved) return;
        doc.RemovedAt = null;
        doc.RemovedBy = null;
        doc.RemovedReason = null;
        Audit(db, doc.Id, AuditAction.Restored, actor, null);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Permanently deletes documents removed before <paramref name="removedBefore"/>. A blob is
    /// deleted only when no remaining version anywhere shares its content. The audit trail is kept.
    /// </summary>
    public async Task<int> PurgeAsync(DateTimeOffset removedBefore, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = (await db.Documents.Include(d => d.Versions)
                .Where(d => d.RemovedAt != null)
                .ToListAsync(ct))
            .Where(d => d.RemovedAt < removedBefore) // filtered in memory: SQLite can't compare DateTimeOffset
            .ToList();
        if (removed.Count == 0) return 0;

        var hashes = removed.SelectMany(d => d.Versions).Select(v => v.Sha256).Distinct().ToList();
        db.Documents.RemoveRange(removed);
        db.AuditEntries.AddRange(removed.Select(d => new AuditEntry
        {
            DocumentId = d.Id, Action = AuditAction.Purged, Actor = actor, At = clock.GetUtcNow(),
            Detail = $"{d.Title} ({d.Versions.Count} versions)",
        }));
        await db.SaveChangesAsync(ct);

        var stillUsed = await db.DocumentVersions.Where(v => hashes.Contains(v.Sha256)).Select(v => v.Sha256).Distinct().ToListAsync(ct);
        foreach (var hash in hashes.Except(stillUsed)) blobs.Delete(hash);
        return removed.Count;
    }

    public async Task<List<Document>> ListAsync(int itemId, bool includeRemoved, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Documents.AsNoTracking()
            .Where(d => d.EquipmentItemId == itemId && (includeRemoved || d.RemovedAt == null))
            .Include(d => d.Versions.OrderByDescending(v => v.VersionNumber))
            .OrderBy(d => d.Title)
            .ToListAsync(ct);
    }

    public async Task<List<AuditEntry>> HistoryAsync(int documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries.AsNoTracking().Where(a => a.DocumentId == documentId).OrderByDescending(a => a.Id).ToListAsync(ct);
    }

    /// <summary>Opens a version's content. Removed documents can't be downloaded until restored.</summary>
    public async Task<(DocumentVersion Version, Stream Content)?> OpenAsync(int documentId, int versionNumber, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var version = await db.DocumentVersions.AsNoTracking().Include(v => v.Document)
            .SingleOrDefaultAsync(v => v.DocumentId == documentId && v.VersionNumber == versionNumber, ct);
        if (version is null || version.Document!.IsRemoved || !blobs.Exists(version.Sha256)) return null;
        return (version, blobs.OpenRead(version.Sha256));
    }

    /// <summary>
    /// Streams the upload to a temp file while hashing it, enforcing the size limit as bytes
    /// arrive rather than after the whole file is in memory.
    /// </summary>
    private async Task<StagedFile> StageAsync(Stream content, string fileName, CancellationToken ct)
    {
        var temp = Path.Combine(options.TempDirectory, $"upload-{Guid.NewGuid():N}");
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var header = new byte[ContentInspector.HeaderBytes];
            var headerLength = 0;
            long size = 0;
            var buffer = new byte[81920];
            await using (var file = File.Create(temp))
            {
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > options.MaxBytes)
                        throw new DocumentException($"Files can be at most {options.MaxBytes / (1024 * 1024)} MB.");
                    var toHeader = Math.Min(read, header.Length - headerLength);
                    Array.Copy(buffer, 0, header, headerLength, toHeader);
                    headerLength += toHeader;
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (size == 0) throw new DocumentException("The file is empty.");
            var kind = ContentInspector.Detect(header.AsSpan(0, headerLength), fileName)
                ?? throw new DocumentException("Only PNG, JPEG, GIF, PDF, and plain-text files can be attached.");
            return new StagedFile(temp, Convert.ToHexStringLower(sha.GetHashAndReset()), size, kind);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private static DocumentVersion NewVersion(StagedFile f, int number, string fileName, string actor, string? comment, DateTimeOffset at) => new()
    {
        VersionNumber = number,
        FileName = CleanFileName(fileName),
        Kind = f.Kind,
        SizeBytes = f.Size,
        Sha256 = f.Sha256,
        UploadedAt = at,
        UploadedBy = actor,
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
    };

    /// <summary>Keeps only the name part ("C:\Users\me\x.pdf" from old browsers becomes "x.pdf").</summary>
    private static string CleanFileName(string name)
    {
        var clean = Path.GetFileName(name.Replace('\\', '/')).Trim();
        if (clean.Length == 0) clean = "file";
        return clean.Length <= 255 ? clean : clean[^255..];
    }

    private static string Required(string value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new DocumentException(message) : value.Trim();

    private static async Task<Document> FindAsync(DocsDbContext db, int id, CancellationToken ct) =>
        await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new DocumentException($"Document {id} does not exist.");

    private async Task<int> CurrentVersionAsync(int documentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Documents.Where(d => d.Id == documentId).Select(d => d.CurrentVersionNumber).SingleAsync(ct);
    }

    private static ConcurrencyConflictException Conflict(int current) =>
        new($"Someone else uploaded version {current} while you were working. Review it, then upload again.");

    /// <summary>Queues an audit entry so it is saved in the same SaveChanges as the change it records.</summary>
    private void Audit(DocsDbContext db, int documentId, AuditAction action, string actor, string? detail) =>
        db.AuditEntries.Add(new AuditEntry { DocumentId = documentId, Action = action, Actor = actor, At = clock.GetUtcNow(), Detail = detail });
}
