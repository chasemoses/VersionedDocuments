namespace VersionedDocuments.Core;

/// <summary>
/// Content-addressed file storage: a blob's key is its SHA-256 hash. The database holds
/// metadata only, which keeps backups small and lets the storage move (e.g. to Azure Blob)
/// without touching the schema.
/// </summary>
public interface IBlobStore
{
    bool Exists(string sha256);

    /// <summary>Moves a fully written temp file into the store. A no-op if the content already exists.</summary>
    void Adopt(string sha256, string tempFilePath);

    Stream OpenRead(string sha256);
    void Delete(string sha256);
}

public sealed class FileSystemBlobStore(string root) : IBlobStore
{
    // Two-level fan-out keeps any one directory from holding millions of files.
    private string PathFor(string sha256) => Path.Combine(root, sha256[..2], sha256[2..4], sha256);

    public bool Exists(string sha256) => File.Exists(PathFor(sha256));

    public void Adopt(string sha256, string tempFilePath)
    {
        var target = PathFor(sha256);
        if (File.Exists(target))
        {
            File.Delete(tempFilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(tempFilePath, target);
    }

    public Stream OpenRead(string sha256) =>
        new FileStream(PathFor(sha256), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public void Delete(string sha256)
    {
        var path = PathFor(sha256);
        if (File.Exists(path)) File.Delete(path);
    }
}
