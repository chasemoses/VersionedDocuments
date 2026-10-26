using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VersionedDocuments.Core;

namespace VersionedDocuments.Tests;

public sealed class DocumentServiceTests : IDisposable
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vdtests-" + Guid.NewGuid().ToString("N"));
    private readonly TestFactory _factory;
    private readonly FileSystemBlobStore _blobs;
    private readonly TestClock _clock = new();
    private readonly DocumentService _service;
    private readonly int _itemId;

    public DocumentServiceTests()
    {
        _connection.Open();
        _factory = new TestFactory(new DbContextOptionsBuilder<DocsDbContext>().UseSqlite(_connection).Options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var item = new EquipmentItem { Tag = "EQ-1001", Name = "Projector, Room 120" };
            db.EquipmentItems.Add(item);
            db.SaveChanges();
            _itemId = item.Id;
        }
        _blobs = new FileSystemBlobStore(Path.Combine(_root, "blobs"));
        Directory.CreateDirectory(Path.Combine(_root, "tmp"));
        _service = new DocumentService(_factory, _blobs, new DocumentOptions { TempDirectory = Path.Combine(_root, "tmp"), MaxBytes = 1024 }, _clock);
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static MemoryStream S(byte[] bytes) => new(bytes);
    private static MemoryStream S(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Create_stores_version_one_with_detected_kind_and_hash()
    {
        var doc = await _service.CreateAsync(_itemId, "Warranty", "warranty.pdf", S(Pdf), "alice");

        var v = Assert.Single(doc.Versions);
        Assert.Equal(1, v.VersionNumber);
        Assert.Equal(ContentKind.Pdf, v.Kind);
        Assert.Equal(Pdf.Length, v.SizeBytes);
        Assert.True(_blobs.Exists(v.Sha256));
    }

    [Fact]
    public async Task New_version_keeps_old_versions_downloadable()
    {
        var doc = await _service.CreateAsync(_itemId, "Manual", "manual.txt", S("first"), "alice");
        await _service.AddVersionAsync(doc.Id, 1, "manual.txt", S("second"), "bob", "fixed typo");

        var listed = Assert.Single(await _service.ListAsync(_itemId, includeRemoved: false));
        Assert.Equal(2, listed.CurrentVersionNumber);
        Assert.Equal([2, 1], listed.Versions.Select(v => v.VersionNumber));
        Assert.Equal("first", await ReadAsync(doc.Id, 1));
        Assert.Equal("second", await ReadAsync(doc.Id, 2));
    }

    [Fact]
    public async Task Stale_expected_version_is_rejected()
    {
        var doc = await _service.CreateAsync(_itemId, "Manual", "m.txt", S("v1"), "alice");
        await _service.AddVersionAsync(doc.Id, 1, "m.txt", S("v2 from bob"), "bob");

        // Alice still has version 1 on screen.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _service.AddVersionAsync(doc.Id, 1, "m.txt", S("v2 from alice"), "alice"));
        Assert.Contains("version 2", ex.Message);
    }

    [Fact]
    public async Task Concurrency_token_catches_a_race_the_precheck_cannot_see()
    {
        var doc = await _service.CreateAsync(_itemId, "Manual", "m.txt", S("v1"), "alice");

        await using var slow = _factory.CreateDbContext();
        var stale = await slow.Documents.SingleAsync(d => d.Id == doc.Id); // read version 1
        await _service.AddVersionAsync(doc.Id, 1, "m.txt", S("v2"), "bob");  // someone commits version 2

        stale.CurrentVersionNumber = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => slow.SaveChangesAsync());
    }

    [Fact]
    public async Task Identical_content_is_stored_once()
    {
        var a = await _service.CreateAsync(_itemId, "Photo A", "a.png", S(Png), "alice");
        var b = await _service.CreateAsync(_itemId, "Photo B", "copy.png", S(Png), "bob");

        Assert.Equal(a.Versions[0].Sha256, b.Versions[0].Sha256);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "blobs"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("invoice.pdf", new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]         // Windows executable renamed to .pdf
    [InlineData("notes.txt", new byte[] { 0x41, 0x00, 0x42 })]                 // binary with a .txt name
    [InlineData("data.bin", new byte[] { 0x41, 0x42, 0x43 })]                  // text, but not a text extension
    public async Task Disallowed_content_is_rejected_and_nothing_is_stored(string name, byte[] content)
    {
        await Assert.ThrowsAsync<DocumentException>(() => _service.CreateAsync(_itemId, "X", name, S(content), "alice"));

        Assert.False(Directory.Exists(Path.Combine(_root, "blobs")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task Oversized_and_empty_files_are_rejected()
    {
        await Assert.ThrowsAsync<DocumentException>(() => _service.CreateAsync(_itemId, "Big", "big.txt", S(new string('a', 2000)), "alice"));
        await Assert.ThrowsAsync<DocumentException>(() => _service.CreateAsync(_itemId, "Empty", "e.txt", S(""), "alice"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task File_name_path_is_stripped()
    {
        var doc = await _service.CreateAsync(_itemId, "Scan", @"C:\Users\someone\Desktop\scan.pdf", S(Pdf), "alice");
        Assert.Equal("scan.pdf", doc.Versions[0].FileName);
    }

    [Fact]
    public async Task Remove_hides_and_blocks_download_restore_brings_it_back()
    {
        var doc = await _service.CreateAsync(_itemId, "Old manual", "m.txt", S("content"), "alice");

        await _service.RemoveAsync(doc.Id, "bob", "Superseded by vendor PDF");
        Assert.Empty(await _service.ListAsync(_itemId, includeRemoved: false));
        Assert.Single(await _service.ListAsync(_itemId, includeRemoved: true));
        Assert.Null(await _service.OpenAsync(doc.Id, 1));
        await Assert.ThrowsAsync<DocumentException>(() => _service.AddVersionAsync(doc.Id, 1, "m.txt", S("x"), "bob"));

        await _service.RestoreAsync(doc.Id, "carol");
        Assert.Equal("content", await ReadAsync(doc.Id, 1));
    }

    [Fact]
    public async Task Remove_requires_a_reason()
    {
        var doc = await _service.CreateAsync(_itemId, "Manual", "m.txt", S("x"), "alice");
        await Assert.ThrowsAsync<DocumentException>(() => _service.RemoveAsync(doc.Id, "bob", "  "));
    }

    [Fact]
    public async Task Purge_deletes_only_old_removals_and_keeps_shared_blobs()
    {
        var oldDoc = await _service.CreateAsync(_itemId, "Old", "old.png", S(Png), "alice");
        var sharer = await _service.CreateAsync(_itemId, "Same image", "same.png", S(Png), "alice");
        var unique = await _service.AddVersionAsync(oldDoc.Id, 1, "old2.txt", S("only here"), "alice");
        await _service.RemoveAsync(oldDoc.Id, "bob", "Duplicate");

        _clock.Advance(TimeSpan.FromDays(31));
        var recent = await _service.CreateAsync(_itemId, "Recent", "r.txt", S("recent"), "alice");
        await _service.RemoveAsync(recent.Id, "bob", "Mistake");

        var purged = await _service.PurgeAsync(_clock.GetUtcNow().AddDays(-30), "admin");

        Assert.Equal(1, purged);
        Assert.False(_blobs.Exists(unique.Sha256));                 // only the purged doc used it
        Assert.True(_blobs.Exists(sharer.Versions[0].Sha256));      // another document still uses it
        Assert.Single(await _service.ListAsync(_itemId, includeRemoved: true), d => d.Id == recent.Id);
        Assert.Contains(await _service.HistoryAsync(oldDoc.Id), a => a.Action == AuditAction.Purged);
    }

    [Fact]
    public async Task Every_change_is_audited_with_actor()
    {
        var doc = await _service.CreateAsync(_itemId, "Manual", "m.txt", S("1"), "alice");
        await _service.AddVersionAsync(doc.Id, 1, "m.txt", S("2"), "bob");
        await _service.RemoveAsync(doc.Id, "carol", "Wrong item");
        await _service.RestoreAsync(doc.Id, "dave");

        var history = await _service.HistoryAsync(doc.Id);
        Assert.Equal(
            [(AuditAction.Restored, "dave"), (AuditAction.Removed, "carol"), (AuditAction.VersionAdded, "bob"), (AuditAction.Created, "alice")],
            history.Select(h => (h.Action, h.Actor)));
    }

    private async Task<string> ReadAsync(int documentId, int version)
    {
        var opened = await _service.OpenAsync(documentId, version) ?? throw new InvalidOperationException("not found");
        await using var s = opened.Content;
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }

    private sealed class TestFactory(DbContextOptions<DocsDbContext> options) : IDbContextFactory<DocsDbContext>
    {
        public DocsDbContext CreateDbContext() => new(options);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
