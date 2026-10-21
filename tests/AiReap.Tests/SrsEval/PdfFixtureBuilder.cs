using System.Buffers.Binary;
using System.IO.Compression;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AiReap.Tests.SrsEval;

// Phase 0 — synthetic PDF fixtures built in memory with PdfPig's writer (the PdfPig build AiReap
// already references), so no binary file is committed and nothing is downloaded. Text uses the
// standard Helvetica font, which covers WinAnsi characters only; fixtures keep to plain ASCII.
public static class PdfFixtureBuilder
{
    private const double PageWidth = 595, PageHeight = 842, Left = 50, Top = 790, LineHeight = 16;

    // One text line per entry; each inner list is one page.
    public static byte[] Text(IEnumerable<IReadOnlyList<string>> pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageWidth, PageHeight);
            var y = Top;
            foreach (var line in lines)
            {
                page.AddText(line, 11, new PdfPoint(Left, y), font);
                y -= LineHeight;
            }
        }

        return builder.Build();
    }

    // A "scanned" page: an image and no text layer at all.
    public static byte[] ImageOnly(int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        var png = SolidPng(120, 160, 0xE0);
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(PageWidth, PageHeight);
            page.AddPng(png, new PdfRectangle(Left, 200, PageWidth - Left, Top));
        }

        return builder.Build();
    }

    // A text page followed by an image-only page.
    public static byte[] TextThenImage(IReadOnlyList<string> textLines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(PageWidth, PageHeight);
        var y = Top;
        foreach (var line in textLines)
        {
            first.AddText(line, 11, new PdfPoint(Left, y), font);
            y -= LineHeight;
        }

        var second = builder.AddPage(PageWidth, PageHeight);
        second.AddPng(SolidPng(120, 160, 0xE0), new PdfRectangle(Left, 200, PageWidth - Left, Top));
        return builder.Build();
    }

    // A minimal, deterministic 8-bit greyscale PNG (signature, IHDR, one zlib IDAT, IEND), written here
    // so the scanned-page fixture needs no image file or imaging library.
    public static byte[] SolidPng(int width, int height, byte grey)
    {
        var raw = new byte[height * (width + 1)];
        for (var row = 0; row < height; row++)
        {
            raw[row * (width + 1)] = 0; // filter: none
            Array.Fill(raw, grey, row * (width + 1) + 1, width);
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = buffer.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // colour type: greyscale
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data)) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
