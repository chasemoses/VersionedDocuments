using System.Text;
using Microsoft.EntityFrameworkCore;
using VersionedDocuments.Core;

namespace VersionedDocuments.Web;

public sealed class CurrentUser
{
    public static readonly string[] DemoUsers = ["alex.facilities", "jordan.av", "sam.it"];
    public string Name { get; set; } = DemoUsers[0];
}

/// <summary>Creates the database and a few items with history so the demo has something to show.</summary>
public static class DemoSeed
{
    public static async Task RunAsync(IServiceProvider services, bool reset)
    {
        var factory = services.GetRequiredService<IDbContextFactory<DocsDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            if (reset) await db.Database.EnsureDeletedAsync();
            if (!await db.Database.EnsureCreatedAsync()) return; // already exists: keep user data

            db.EquipmentItems.AddRange(
                new EquipmentItem { Tag = "AV-0142", Name = "Laser projector, Smith 120" },
                new EquipmentItem { Tag = "AV-0388", Name = "Lecture capture camera, Romney 240" },
                new EquipmentItem { Tag = "FAC-2210", Name = "Rooftop air handler, Hart building" },
                new EquipmentItem { Tag = "IT-5521", Name = "Lab printer, Library 2nd floor" });
            await db.SaveChangesAsync();
        }

        var docs = services.GetRequiredService<DocumentService>();
        var (alex, jordan, sam) = (CurrentUser.DemoUsers[0], CurrentUser.DemoUsers[1], CurrentUser.DemoUsers[2]);

        var warranty = await docs.CreateAsync(1, "Warranty certificate", "warranty-AV-0142.pdf", Pdf("Warranty: 3 years parts and labor"), jordan);
        var manual = await docs.CreateAsync(1, "Lamp replacement steps", "lamp-steps.txt", Text("1. Power off\n2. Wait 30 minutes\n3. Replace module"), jordan, "From vendor call");
        await docs.AddVersionAsync(manual.Id, 1, "lamp-steps.txt", Text("1. Power off\n2. Wait 45 minutes (vendor update)\n3. Replace module\n4. Reset hours counter"), alex, "Vendor changed cool-down time");
        await docs.AddVersionAsync(manual.Id, 2, "lamp-steps.md", Text("# Lamp replacement\n1. Power off\n2. Wait 45 minutes\n3. Replace module\n4. Reset hours counter in service menu"), sam, "Added service menu note");
        var old = await docs.CreateAsync(1, "Old install photo notes", "install.txt", Text("Mounted 2019"), alex);
        await docs.RemoveAsync(old.Id, sam, "Duplicate of install record in work order system");

        await docs.CreateAsync(3, "Filter schedule", "filters.csv", Text("month,filter\nJan,MERV-13\nApr,MERV-13\nJul,MERV-13\nOct,MERV-13"), alex);
        await docs.CreateAsync(4, "Toner part numbers", "toner.txt", Text("Black: TN-810\nCyan: TN-811"), sam);
        _ = warranty;
    }

    private static MemoryStream Text(string s) => new(Encoding.UTF8.GetBytes(s));

    /// <summary>A minimal one-page PDF with a line of text.</summary>
    private static MemoryStream Pdf(string line)
    {
        var stream = $"BT /F1 18 Tf 72 720 Td ({line}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()));
    }
}
