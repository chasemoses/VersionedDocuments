using System.Text;
using VersionedDocuments.Core;

namespace VersionedDocuments.Tests;

public class ContentInspectorTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, "x.png", ContentKind.Png)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, "x.pdf", ContentKind.Png)] // name doesn't matter
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "photo", ContentKind.Jpeg)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "a.gif", ContentKind.Gif)]
    public void Binary_types_are_detected_by_signature(byte[] header, string name, ContentKind expected) =>
        Assert.Equal(expected, ContentInspector.Detect(header, name));

    [Fact]
    public void Utf8_text_with_text_extension_is_text() =>
        Assert.Equal(ContentKind.Text, ContentInspector.Detect(Encoding.UTF8.GetBytes("Café, naïve, 日本"), "notes.md"));

    [Fact]
    public void Multibyte_character_cut_at_header_boundary_is_still_text()
    {
        var bytes = Encoding.UTF8.GetBytes("日本");
        Assert.Equal(ContentKind.Text, ContentInspector.Detect(bytes[..^1], "notes.txt"));
    }

    [Theory]
    [InlineData(new byte[] { 0xC3, 0x28, 0x41 })] // invalid UTF-8
    [InlineData(new byte[] { 0x41, 0x00, 0x42 })] // NUL byte: binary content
    public void Invalid_text_is_rejected(byte[] header) =>
        Assert.Null(ContentInspector.Detect(header, "notes.txt"));

    [Fact]
    public void Unknown_binary_without_text_extension_is_rejected() =>
        Assert.Null(ContentInspector.Detect([0x4D, 0x5A, 0x90, 0x00], "setup.pdf"));
}
