using System.Buffers;
using System.Text.Unicode;

namespace VersionedDocuments.Core;

public enum ContentKind { Png, Jpeg, Gif, Pdf, Text }

/// <summary>
/// Decides what a file actually is from its first bytes. The file name and the browser's
/// claimed content type are both chosen by the uploader, so neither is trusted.
/// </summary>
public static class ContentInspector
{
    public const int HeaderBytes = 8192;

    private static readonly (ContentKind Kind, byte[] Magic)[] Signatures =
    [
        (ContentKind.Png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        (ContentKind.Jpeg, [0xFF, 0xD8, 0xFF]),
        (ContentKind.Gif, "GIF8"u8.ToArray()),
        (ContentKind.Pdf, "%PDF-"u8.ToArray()),
    ];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".md", ".log" };

    public static string MimeType(ContentKind kind) => kind switch
    {
        ContentKind.Png => "image/png",
        ContentKind.Jpeg => "image/jpeg",
        ContentKind.Gif => "image/gif",
        ContentKind.Pdf => "application/pdf",
        ContentKind.Text => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    /// <summary>Returns the detected kind, or null when the content is not an allowed type.</summary>
    public static ContentKind? Detect(ReadOnlySpan<byte> header, string fileName)
    {
        foreach (var (kind, magic) in Signatures)
            if (header.StartsWith(magic)) return kind;

        // Text has no signature. Accept it only with a text extension, valid UTF-8, and no NUL bytes,
        // which rules out renamed binaries.
        if (TextExtensions.Contains(Path.GetExtension(fileName)) && LooksLikeText(header))
            return ContentKind.Text;

        return null;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        if (header.Contains((byte)0)) return false;
        // isFinalBlock: false because the header may cut a multi-byte character in half.
        // The decoder reports that case as NeedMoreData, distinct from InvalidData.
        var chars = new char[header.Length];
        var status = Utf8.ToUtf16(header, chars, out _, out _, replaceInvalidSequences: false, isFinalBlock: false);
        return status is OperationStatus.Done or OperationStatus.NeedMoreData;
    }
}
