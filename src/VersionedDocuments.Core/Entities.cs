namespace VersionedDocuments.Core;

/// <summary>Something documents get attached to: a piece of campus equipment.</summary>
public class EquipmentItem
{
    public int Id { get; set; }
    public required string Tag { get; set; }
    public required string Name { get; set; }
    public List<Document> Documents { get; set; } = [];
}

/// <summary>
/// The logical document ("Warranty", "Service manual"). Its content lives in versions;
/// the document itself only knows which version is current and whether it was removed.
/// </summary>
public class Document
{
    public int Id { get; set; }
    public int EquipmentItemId { get; set; }
    public EquipmentItem? EquipmentItem { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }

    /// <summary>
    /// Also the concurrency token: two people uploading "version 3" at the same moment
    /// can't both succeed, because the second save sees a changed value.
    /// </summary>
    public int CurrentVersionNumber { get; set; }

    // Removal is a soft delete. Content stays until an explicit purge.
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovedBy { get; set; }
    public string? RemovedReason { get; set; }
    public bool IsRemoved => RemovedAt is not null;

    public List<DocumentVersion> Versions { get; set; } = [];
}

/// <summary>An immutable uploaded file. Versions are never edited, only added.</summary>
public class DocumentVersion
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public Document? Document { get; set; }
    public int VersionNumber { get; set; }
    public required string FileName { get; set; }
    public ContentKind Kind { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the content; also the blob's key, so identical files are stored once.</summary>
    public required string Sha256 { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public required string UploadedBy { get; set; }
    public string? Comment { get; set; }
}

public enum AuditAction { Created, VersionAdded, Removed, Restored, Purged }

public class AuditEntry
{
    public long Id { get; set; }
    public int DocumentId { get; set; }
    public AuditAction Action { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Detail { get; set; }
}
