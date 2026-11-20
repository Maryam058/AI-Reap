using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiReap.Infrastructure.Files;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiReap.Tests;

// The original PDF/DOCX of an upload, through the API (docs/SOURCE_DOCUMENT_RETENTION.md): kept on
// upload with its text unchanged, served only to project members who may read raw sources, reported as
// not found to anyone else, and removed with its project or after the configured retention period.
// Storage is a temporary directory per test class; documents are built in memory; no AI provider.
public class OriginalDocumentRetentionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aireap-originals-" + Guid.NewGuid().ToString("N"));
    private readonly TestHost _host;
    private ApiUser _ba = null!, _reviewer = null!, _dev = null!, _outsider = null!;

    public OriginalDocumentRetentionTests()
    {
        _host = new TestHost(settings: new Dictionary<string, string>
        {
            ["OriginalDocuments:RootPath"] = _root,
            ["OriginalDocuments:CleanupIntervalMinutes"] = "0", // swept explicitly below
            ["OriginalDocuments:OrphanGraceMinutes"] = "0",
            ["OriginalDocuments:MaxUploadBytes"] = (256 * 1024).ToString(),
        });
    }

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _dev = await _host.RegisterAsync("Developer");
        _outsider = await _host.RegisterAsync("BusinessAnalyst");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly byte[] Pdf = PdfFixtureBuilder.Text(new[]
    {
        (IReadOnlyList<string>)new[] { "1. Introduction", "Leave requests are submitted online." },
        new[] { "12.7 Decline Categories", "The eight decline categories are listed in the decline popup." },
    });

    private static readonly byte[] Docx = DocxFixtureBuilder.Plain(new[] { "3.2 User Roles", "Administrators manage access permissions." });

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<string> ProjectAsync()
    {
        var (status, project) = await _ba.PostAsync("/api/projects", new { name = "Leave", description = "d" });
        Assert.Equal(201, status);
        var projectId = Id(project);
        foreach (var member in new[] { _reviewer, _dev })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, projectId, member)).Status, 200, 201);
        }

        return projectId;
    }

    private static async Task<(int Status, JsonNode? Body)> UploadAsync(ApiUser user, string projectId, string fileName, byte[] content, string mime)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", fileName);
        var response = await user.Http.PostAsync($"/api/projects/{projectId}/requirement-sources/upload", form);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? body = null;
        try { body = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { body = JsonValue.Create(text); }
        return ((int)response.StatusCode, body);
    }

    private static Task<HttpResponseMessage> OriginalAsync(ApiUser user, string sourceId) =>
        user.Http.GetAsync($"/api/requirement-sources/{sourceId}/original");

    private string[] StoredFiles() => Directory.Exists(_root) ? Directory.GetFiles(_root, "*.bin", SearchOption.AllDirectories) : Array.Empty<string>();

    [Fact]
    public async Task A_pdf_upload_keeps_the_original_bytes_and_its_metadata_and_the_text_is_unchanged()
    {
        var projectId = await ProjectAsync();

        var (status, created) = await UploadAsync(_ba, projectId, "Leave SRS.pdf", Pdf, "application/pdf");

        Assert.Equal(201, status);
        var sourceId = Id(created);
        var source = (await _ba.GetAsync($"/api/requirement-sources/{sourceId}")).Body!;
        Assert.Contains("12.7 Decline Categories", source["rawText"]!.GetValue<string>());
        var original = source["originalDocument"]!;
        Assert.Equal("pdf", original["format"]!.GetValue<string>());
        Assert.Equal(Pdf.Length, original["length"]!.GetValue<long>());
        Assert.Equal(2, original["pageLineStarts"]!.AsArray().Count);
        var json = source.ToJsonString();
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase); // no storage location
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);

        using var response = await OriginalAsync(_ba, sourceId);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);

        var stored = Assert.Single(StoredFiles());
        Assert.DoesNotContain("Leave", stored); // stored under a server key, not the file name
        Assert.Equal(1, await _host.ScalarAsync(
            $"SELECT COUNT(*) FROM RequirementSourceFiles WHERE RequirementSourceId = '{sourceId}' AND Sha256 = '{Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant()}'"));
    }

    [Fact]
    public async Task A_docx_upload_keeps_the_original_and_serves_it_as_word()
    {
        var projectId = await ProjectAsync();

        var (status, created) = await UploadAsync(_ba, projectId, "brief.docx", Docx, "application/octet-stream");

        Assert.Equal(201, status);
        using var response = await OriginalAsync(_reviewer, Id(created));
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Docx, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_file_whose_content_is_not_what_its_name_says_is_refused_and_leaves_nothing_behind()
    {
        var projectId = await ProjectAsync();

        var (pdfAsDocx, _) = await UploadAsync(_ba, projectId, "srs.docx", Pdf, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        var (textAsPdf, _) = await UploadAsync(_ba, projectId, "srs.pdf", Encoding.UTF8.GetBytes("not a pdf"), "application/pdf");
        var (exe, _) = await UploadAsync(_ba, projectId, "srs.exe", Encoding.UTF8.GetBytes("MZ"), "application/pdf");

        Assert.Equal(400, pdfAsDocx);
        Assert.Equal(400, textAsPdf);
        Assert.Equal(400, exe);
        Assert.Equal(0, await _host.ScalarAsync($"SELECT COUNT(*) FROM RequirementSources WHERE ProjectId = '{projectId}'"));
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task An_upload_over_the_configured_limit_is_refused()
    {
        var projectId = await ProjectAsync();
        var big = new byte[300 * 1024];
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(big, 0);

        var (status, _) = await UploadAsync(_ba, projectId, "big.pdf", big, "application/pdf");

        Assert.Equal(413, status);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task Only_members_who_may_read_raw_sources_get_the_original_and_another_projects_source_looks_absent()
    {
        var projectId = await ProjectAsync();
        var sourceId = Id((await UploadAsync(_ba, projectId, "srs.pdf", Pdf, "application/pdf")).Body);

        using var developer = await OriginalAsync(_dev, sourceId);
        using var outsider = await OriginalAsync(_outsider, sourceId);
        using var unknown = await OriginalAsync(_outsider, Guid.NewGuid().ToString());

        Assert.Equal(403, (int)developer.StatusCode); // S2: a Developer works from approved artifacts
        Assert.DoesNotContain("%PDF", await developer.Content.ReadAsStringAsync());
        // Not a member: exactly the answer for a source that does not exist.
        Assert.Equal(404, (int)outsider.StatusCode);
        Assert.Equal(404, (int)unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await outsider.Content.ReadAsStringAsync());
        // And a Developer's source read never carries the original's metadata.
        Assert.Equal(403, (await _dev.GetAsync($"/api/requirement-sources/{sourceId}")).Status);
    }

    [Fact]
    public async Task A_source_without_a_kept_original_or_with_a_missing_file_gets_a_controlled_answer()
    {
        var projectId = await ProjectAsync();
        var (_, pasted) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 0, rawText = "FR-001: Book.", originalFileName = (string?)null });
        var txt = Id((await UploadAsync(_ba, projectId, "notes.txt", Encoding.UTF8.GetBytes("FR-001: Book."), "text/plain")).Body);
        var pdf = Id((await UploadAsync(_ba, projectId, "srs.pdf", Pdf, "application/pdf")).Body);

        foreach (var legacy in new[] { Id(pasted), txt })
        {
            using var response = await OriginalAsync(_ba, legacy);
            Assert.Equal(404, (int)response.StatusCode);
            Assert.Contains("original-not-retained", await response.Content.ReadAsStringAsync());
            Assert.Null((await _ba.GetAsync($"/api/requirement-sources/{legacy}")).Body!["originalDocument"]);
        }

        File.Delete(Assert.Single(StoredFiles()));
        using var missing = await OriginalAsync(_ba, pdf);
        Assert.Equal(404, (int)missing.StatusCode);
        Assert.Contains("original-unavailable", await missing.Content.ReadAsStringAsync());
        Assert.Equal(200, (await _ba.GetAsync($"/api/requirement-sources/{pdf}")).Status); // the text still is
    }

    [Fact]
    public async Task A_corrupted_file_fails_its_integrity_check()
    {
        var projectId = await ProjectAsync();
        var sourceId = Id((await UploadAsync(_ba, projectId, "srs.pdf", Pdf, "application/pdf")).Body);
        var stored = Assert.Single(StoredFiles());
        var bytes = await File.ReadAllBytesAsync(stored);
        bytes[^10] ^= 0xFF;
        await File.WriteAllBytesAsync(stored, bytes);

        using var response = await OriginalAsync(_ba, sourceId);

        Assert.Equal(404, (int)response.StatusCode);
        Assert.Contains("original-unavailable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deleting_the_project_removes_its_originals_and_the_sweep_removes_orphans_only()
    {
        var doomed = await ProjectAsync();
        var kept = await ProjectAsync();
        await UploadAsync(_ba, doomed, "srs.pdf", Pdf, "application/pdf");
        var keptSource = Id((await UploadAsync(_ba, kept, "brief.docx", Docx, "application/octet-stream")).Body);
        Assert.Equal(2, StoredFiles().Length);

        var (deleted, _) = await _ba.SendAsync(HttpMethod.Delete, $"/api/projects/{doomed}");

        Assert.InRange(deleted, 200, 204);
        var remaining = Assert.Single(StoredFiles());

        // An orphan: a stored file no source references (e.g. a clean-up that failed).
        var orphan = Path.Combine(_root, "ab", "ab" + new string('0', 30) + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllBytesAsync(orphan, new byte[] { 1 });
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(remaining, DateTime.UtcNow.AddHours(-2));

        await Janitor().SweepAsync(DateTime.UtcNow, default);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(remaining));
        using var response = await OriginalAsync(_ba, keptSource);
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task With_a_retention_period_old_originals_are_deleted_and_the_source_text_stays()
    {
        var projectId = await ProjectAsync();
        var sourceId = Id((await UploadAsync(_ba, projectId, "srs.pdf", Pdf, "application/pdf")).Body);
        var options = _host.Factory.Services.GetRequiredService<AiReap.Application.Files.OriginalDocumentOptions>();
        options.RetentionDays = 30;
        try
        {
            await Janitor().SweepAsync(DateTime.UtcNow.AddDays(10), default); // not yet due
            Assert.Single(StoredFiles());

            await Janitor().SweepAsync(DateTime.UtcNow.AddDays(31), default);
        }
        finally
        {
            options.RetentionDays = null;
        }

        Assert.Empty(StoredFiles());
        using var response = await OriginalAsync(_ba, sourceId);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Contains("original-not-retained", await response.Content.ReadAsStringAsync());
        Assert.Contains("12.7 Decline Categories", (await _ba.GetAsync($"/api/requirement-sources/{sourceId}")).Body!["rawText"]!.GetValue<string>());
    }

    private OriginalDocumentJanitor Janitor() =>
        _host.Factory.Services.GetServices<IHostedService>().OfType<OriginalDocumentJanitor>().Single();
}
