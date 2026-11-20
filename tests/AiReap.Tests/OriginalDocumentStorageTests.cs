using System.IO.Compression;
using System.Text;
using AiReap.Application.Ai;
using AiReap.Application.Files;
using AiReap.Infrastructure.Files;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// Keeping the original PDF/DOCX of an upload (docs/SOURCE_DOCUMENT_RETENTION.md), offline: no SQL
// Server, no AI provider, no network. Storage is a temporary directory or an in-memory fake; documents
// are built in memory (PdfFixtureBuilder, DocxFixtureBuilder). The API-level behaviour (who may read
// an original, deletion) is in OriginalDocumentRetentionTests, which needs the SQL test database.
public class OriginalDocumentStorageTests : IDisposable
{
    private static readonly DocumentTextExtractor Extractor = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aireap-originals-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static async Task<string> PlainExtractAsync(byte[] bytes, string fileName)
    {
        using var stream = new MemoryStream(bytes);
        return await Extractor.ExtractAsync(stream, fileName);
    }

    // ---- validation and extraction --------------------------------------------------------------

    [Fact]
    public async Task A_pdf_is_validated_and_its_text_is_unchanged_with_the_line_each_page_starts_on()
    {
        var pdf = PdfFixtureBuilder.Text(new[]
        {
            (IReadOnlyList<string>)new[] { "1. Introduction", "The clinic books appointments." },
            new[] { "2. Booking", "FR-001: Patients book online.", "FR-002: Staff confirm." },
            new[] { "3. Security" },
        });

        var extracted = await Extractor.ExtractDocumentAsync(pdf, "srs.pdf");

        Assert.Equal(OriginalDocumentFormats.Pdf, extracted.Format);
        Assert.Equal(await PlainExtractAsync(pdf, "srs.pdf"), extracted.Text); // what analysis and RAG see is unchanged
        var lines = SourceReferenceIndex.Lines(extracted.Text);
        Assert.Equal(3, extracted.PageLineStarts!.Count);
        Assert.Equal("1. Introduction", lines[extracted.PageLineStarts[0]]);
        Assert.Equal("2. Booking", lines[extracted.PageLineStarts[1]]);
        Assert.Equal("3. Security", lines[extracted.PageLineStarts[2]]);
    }

    [Fact]
    public async Task A_docx_is_validated_and_its_text_is_unchanged()
    {
        var docx = DocxFixtureBuilder.Plain(new[] { "3.2 User Roles", "Administrators manage access." });

        var extracted = await Extractor.ExtractDocumentAsync(docx, "brief.DOCX");

        Assert.Equal(OriginalDocumentFormats.Docx, extracted.Format);
        Assert.Null(extracted.PageLineStarts);
        Assert.Equal(await PlainExtractAsync(docx, "brief.docx"), extracted.Text);
    }

    [Fact]
    public async Task A_txt_upload_is_text_only_and_keeps_no_original()
    {
        var extracted = await Extractor.ExtractDocumentAsync(Encoding.UTF8.GetBytes("FR-001: Book."), "notes.txt");

        Assert.Equal("FR-001: Book.", extracted.Text);
        Assert.Null(extracted.Format);
    }

    public static TheoryData<string, byte[]> Mislabelled() => new()
    {
        { "srs.pdf", DocxFixtureBuilder.Plain(new[] { "Not a PDF" }) },
        { "srs.pdf", Encoding.UTF8.GetBytes("just text, named .pdf") },
        { "srs.docx", PdfFixtureBuilder.Text(new[] { (IReadOnlyList<string>)new[] { "Not a Word file" } }) },
        { "srs.docx", Encoding.UTF8.GetBytes("PK but not really a zip") },
        { "srs.docx", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 } }, // .doc / encrypted
        { "notes.txt", PdfFixtureBuilder.Text(new[] { (IReadOnlyList<string>)new[] { "PDF named .txt" } }) },
        { "notes.txt", Encoding.ASCII.GetBytes("MZ\u0090\u0000 an executable") },
        { "srs.exe", Encoding.UTF8.GetBytes("anything") },
    };

    [Theory]
    [MemberData(nameof(Mislabelled))]
    public async Task Content_that_is_not_what_its_name_says_is_refused(string fileName, byte[] content)
    {
        var ex = await Assert.ThrowsAsync<InvalidDocumentException>(() => Extractor.ExtractDocumentAsync(content, fileName));
        Assert.DoesNotContain("Not a", ex.Message); // the message never quotes the file
    }

    [Fact]
    public async Task A_damaged_pdf_is_refused_without_quoting_it()
    {
        var damaged = Encoding.ASCII.GetBytes("%PDF-1.7\nsecret contents of a broken file\n%%EOF");

        var ex = await Assert.ThrowsAsync<InvalidDocumentException>(() => Extractor.ExtractDocumentAsync(damaged, "srs.pdf"));

        Assert.DoesNotContain("secret", ex.Message);
    }

    // A small upload whose ZIP expands past the limit is refused from its declared sizes, before any part
    // is inflated. The knowledge-document upload goes through this same check (DocumentsController).
    [Fact]
    public async Task A_word_package_that_expands_past_the_limit_is_refused_before_it_is_read()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var types = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
            {
                types.Write("<Types/>");
            }

            using var big = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 201; i++) big.Write(zeros);
        }

        Assert.True(buffer.Length < 5 * 1024 * 1024); // within the upload limit
        var ex = await Assert.ThrowsAsync<InvalidDocumentException>(() => Extractor.ExtractDocumentAsync(buffer.ToArray(), "srs.docx"));
        Assert.Contains("expands to more", ex.Message);
    }

    [Fact]
    public async Task A_zip_that_is_not_a_word_package_is_refused()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            entry.Write("hello");
        }

        await Assert.ThrowsAsync<InvalidDocumentException>(() => Extractor.ExtractDocumentAsync(buffer.ToArray(), "srs.docx"));
    }

    [Fact]
    public async Task A_macro_enabled_word_document_is_refused()
    {
        // A plain DOCX relabelled as macro-enabled in [Content_Types].xml - what a .docm renamed to .docx is.
        var docx = DocxFixtureBuilder.Plain(new[] { "Body" });
        using var buffer = new MemoryStream();
        buffer.Write(docx);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            var types = zip.GetEntry("[Content_Types].xml")!;
            string xml;
            using (var reader = new StreamReader(types.Open())) xml = reader.ReadToEnd();
            types.Delete();
            xml = xml.Replace("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                "application/vnd.ms-word.document.macroEnabled.main+xml");
            using var writer = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open());
            writer.Write(xml);
        }

        await Assert.ThrowsAsync<InvalidDocumentException>(() => Extractor.ExtractDocumentAsync(buffer.ToArray(), "srs.docx"));
    }

    [Fact]
    public void Signatures_are_read_from_the_content()
    {
        Assert.Equal(DocumentSignature.Kind.Pdf, DocumentSignature.Detect("%PDF-1.7"u8));
        Assert.Equal(DocumentSignature.Kind.Pdf, DocumentSignature.Detect("\n\n%PDF-1.4"u8)); // a little leading junk
        Assert.Equal(DocumentSignature.Kind.Zip, DocumentSignature.Detect("PK\u0003\u0004rest"u8));
        Assert.Equal(DocumentSignature.Kind.Executable, DocumentSignature.Detect("MZ"u8));
        Assert.Equal(DocumentSignature.Kind.Unknown, DocumentSignature.Detect("FR-001: plain text"u8));
    }

    // ---- private file storage --------------------------------------------------------------------

    [Fact]
    public async Task The_store_keeps_bytes_under_a_server_key_outside_any_served_folder()
    {
        var store = new FileSystemOriginalDocumentStore(_root);
        var key = OriginalDocumentKeys.NewKey();
        var bytes = new byte[] { 1, 2, 3, 4 };

        await store.SaveAsync(key, bytes);

        Assert.Equal(bytes, await store.ReadAsync(key));
        var file = Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(key + ".bin", Path.GetFileName(file)); // never the uploaded file's name
        await store.DeleteAsync(key);
        Assert.Null(await store.ReadAsync(key));
        await store.DeleteAsync(key); // deleting twice is harmless
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\Windows\\win.ini")]
    [InlineData("srs.pdf")]
    [InlineData("C:\\temp\\x")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")] // upper case is not a key NewKey makes
    [InlineData("")]
    public async Task The_store_refuses_any_key_it_did_not_make(string key)
    {
        var store = new FileSystemOriginalDocumentStore(_root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(key, new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(key));
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any());
    }

    [Fact]
    public async Task The_store_never_overwrites_a_key_and_clears_interrupted_writes()
    {
        var store = new FileSystemOriginalDocumentStore(_root);
        var key = OriginalDocumentKeys.NewKey();
        await store.SaveAsync(key, new byte[] { 1 });

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(key, new byte[] { 2 }));
        Assert.Equal(new byte[] { 1 }, await store.ReadAsync(key));

        // A write that died before its rename, and a file this store did not write.
        var folder = Path.Combine(_root, key[..2]);
        var temp = Path.Combine(folder, key + ".bin.abc.tmp");
        var foreign = Path.Combine(folder, "notes.docx");
        await File.WriteAllBytesAsync(temp, new byte[] { 9 });
        await File.WriteAllBytesAsync(foreign, new byte[] { 9 });
        foreach (var f in new[] { temp, foreign, Path.Combine(folder, key + ".bin") }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));

        var keys = await store.ListKeysWrittenBeforeAsync(DateTime.UtcNow);

        Assert.Equal(new[] { key }, keys);
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(foreign)); // left alone
    }

    // ---- upload consistency ------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_database_save_removes_the_stored_file()
    {
        var store = new FakeStore();
        var key = OriginalDocumentKeys.NewKey();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OriginalDocumentIntake.StoreThenCommitAsync(
            store, key, new byte[] { 1, 2 }, () => throw new InvalidOperationException("save failed"), NullLogger.Instance, default));

        Assert.Equal("save failed", ex.Message);
        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task A_cancelled_save_removes_the_stored_file_too()
    {
        var store = new FakeStore();

        await Assert.ThrowsAsync<OperationCanceledException>(() => OriginalDocumentIntake.StoreThenCommitAsync(
            store, OriginalDocumentKeys.NewKey(), new byte[] { 1 }, () => throw new OperationCanceledException(), NullLogger.Instance, default));

        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task When_the_clean_up_fails_too_the_save_failure_is_still_what_is_reported()
    {
        var store = new FakeStore { FailDeletes = true };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OriginalDocumentIntake.StoreThenCommitAsync(
            store, OriginalDocumentKeys.NewKey(), new byte[] { 1 }, () => throw new InvalidOperationException("save failed"), NullLogger.Instance, default));

        Assert.Equal("save failed", ex.Message);
        Assert.Single(store.Files); // an orphan, which OrphanSweep removes later
    }

    [Fact]
    public async Task A_failed_file_write_never_reaches_the_database()
    {
        var store = new FakeStore { FailSaves = true };
        var committed = false;

        await Assert.ThrowsAsync<IOException>(() => OriginalDocumentIntake.StoreThenCommitAsync(
            store, OriginalDocumentKeys.NewKey(), new byte[] { 1 }, () => { committed = true; return Task.CompletedTask; }, NullLogger.Instance, default));

        Assert.False(committed);
    }

    [Fact]
    public async Task A_successful_upload_keeps_the_file()
    {
        var store = new FakeStore();
        var key = OriginalDocumentKeys.NewKey();

        await OriginalDocumentIntake.StoreThenCommitAsync(store, key, new byte[] { 7 }, () => Task.CompletedTask, NullLogger.Instance, default);

        Assert.Equal(new byte[] { 7 }, store.Files[key]);
    }

    // ---- orphan clean-up --------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_removes_only_files_no_source_references()
    {
        var store = new FakeStore();
        var kept = OriginalDocumentKeys.NewKey();
        var orphan = OriginalDocumentKeys.NewKey();
        store.Files[kept] = new byte[] { 1 };
        store.Files[orphan] = new byte[] { 2 };

        var removed = await OrphanSweep.RunAsync(store, "db-1", (_, _) => Task.FromResult<ISet<string>>(new HashSet<string> { kept }), DateTime.UtcNow, default);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { kept }, store.Files.Keys);
    }

    [Fact]
    public async Task The_sweep_removes_nothing_when_the_references_cannot_be_read()
    {
        var store = new FakeStore();
        store.Files[OriginalDocumentKeys.NewKey()] = new byte[] { 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => OrphanSweep.RunAsync(
            store, "db-1", (_, _) => throw new InvalidOperationException("Invalid object name 'RequirementSourceFiles'."), DateTime.UtcNow, default));

        Assert.Single(store.Files);
    }

    [Fact]
    public async Task The_sweep_never_touches_a_store_that_belongs_to_another_database()
    {
        var store = new FakeStore { Owner = "dev-database" };
        store.Files[OriginalDocumentKeys.NewKey()] = new byte[] { 1 };

        // A test host or second environment pointed at the same directory: none of its files are in this
        // database, but they are not orphans.
        var removed = await OrphanSweep.RunAsync(store, "test-database", (_, _) => Task.FromResult<ISet<string>>(new HashSet<string>()), DateTime.UtcNow, default);

        Assert.Null(removed);
        Assert.Single(store.Files);
    }

    [Fact]
    public async Task A_store_directory_is_claimed_by_the_first_database_and_refused_to_any_other()
    {
        var store = new FileSystemOriginalDocumentStore(_root);

        Assert.True(await store.IsOwnedByAsync("db-a"));
        Assert.True(await store.IsOwnedByAsync("db-a"));
        Assert.False(await store.IsOwnedByAsync("db-b"));
        Assert.False(await new FileSystemOriginalDocumentStore(_root).IsOwnedByAsync("db-b"));
    }

    [Fact]
    public void Without_a_configured_directory_each_database_gets_its_own()
    {
        var dev = FileSystemOriginalDocumentStore.DefaultRoot("Server=.;Database=AiReapDev;Trusted_Connection=True");
        var test = FileSystemOriginalDocumentStore.DefaultRoot("Server=.;Database=AiReapTest_42;Trusted_Connection=True");
        var odd = FileSystemOriginalDocumentStore.DefaultRoot(@"Server=.;Database=..\..\evil;Trusted_Connection=True");

        Assert.EndsWith(Path.Combine("AiReap", "source-documents", "AiReapDev"), dev);
        Assert.NotEqual(dev, test);
        Assert.DoesNotContain("evil", odd); // a name unsafe as a folder is replaced by a hash
        Assert.Equal(Path.GetDirectoryName(dev), Path.GetDirectoryName(odd));
    }

    private sealed class FakeStore : IOriginalDocumentStore
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public bool FailSaves { get; init; }
        public bool FailDeletes { get; init; }

        public Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            if (FailSaves) throw new IOException("disk full");
            Files.Add(key, content.ToArray());
            return Task.CompletedTask;
        }

        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.TryGetValue(key, out var bytes) ? bytes : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FailDeletes) throw new IOException("locked");
            Files.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysWrittenBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Files.Keys.ToList());

        public string Owner { get; init; } = "db-1";

        public Task<bool> IsOwnedByAsync(string owner, CancellationToken cancellationToken = default) => Task.FromResult(owner == Owner);
    }
}
