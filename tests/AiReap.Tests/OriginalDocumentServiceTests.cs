using AiReap.Application.Common;
using AiReap.Application.Files;
using AiReap.Application.RequirementSources;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiReap.Tests;

// RequirementSourceService's rules for kept originals, offline (InMemoryAiReapDb, an in-memory store,
// no SQL Server): an upload keeps the file only with its row, and an original is returned only to a
// member of the source's project who may read raw sources, after an integrity check. The same rules
// over HTTP and SQL Server are in OriginalDocumentRetentionTests.
public class OriginalDocumentServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid(), ProjectB = Guid.NewGuid();
    private static readonly byte[] PdfBytes = "%PDF-1.7 pretend pdf"u8.ToArray();

    private sealed class User(string id, params string[] roles) : ICurrentUser
    {
        public string UserId => id;
        public bool IsInRole(string role) => roles.Contains(role);
    }

    // Membership of ProjectA only, as for a BA who was never added to ProjectB.
    private sealed class MemberOf(params Guid[] projects) : IProjectAccessService
    {
        public Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(projects.Contains(projectId));

        public Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            projects.Contains(projectId) ? Task.CompletedTask : throw new ProjectAccessDeniedException(projectId);
    }

    private sealed class MemoryStore : IOriginalDocumentStore
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            Files.Add(key, content.ToArray());
            return Task.CompletedTask;
        }

        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.TryGetValue(key, out var b) ? b.ToArray() : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Files.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysWrittenBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Files.Keys.ToList());

        public Task<bool> IsOwnedByAsync(string owner, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private readonly InMemoryAiReapDb _db = new();
    private readonly MemoryStore _store = new();

    private RequirementSourceService Service(string role = Roles.BusinessAnalyst, bool enabled = true, params Guid[] memberOf) =>
        new(_db, new User("u1", role), new MemberOf(memberOf.Length == 0 ? new[] { ProjectA } : memberOf), _store,
            new OriginalDocumentOptions { Enabled = enabled });

    private Task<RequirementSourceResponse> UploadPdfAsync(Guid project) =>
        Service(memberOf: new[] { ProjectA, ProjectB })
            .CreateFromUploadAsync(project, "srs.pdf", "1. Intro\n2. Booking", new OriginalDocumentUpload(PdfBytes, OriginalDocumentFormats.Pdf, new[] { 0, 1 }));

    [Fact]
    public async Task An_upload_keeps_its_text_and_the_original_under_a_server_key()
    {
        var created = await UploadPdfAsync(ProjectA);

        Assert.Equal("1. Intro\n2. Booking", created.RawText);
        Assert.Equal("pdf", created.OriginalDocument!.Format);
        Assert.Equal(PdfBytes.Length, created.OriginalDocument.Length);
        Assert.Equal(new[] { 0, 1 }, created.OriginalDocument.PageLineStarts);
        var row = Assert.Single(InMemoryAiReapDb.Rows(_db.RequirementSourceFiles));
        Assert.Equal(created.Id, row.RequirementSourceId);
        Assert.True(OriginalDocumentKeys.IsValid(row.StorageKey));
        Assert.Equal(OriginalDocumentFormats.Sha256(PdfBytes), row.Sha256);
        Assert.Equal(PdfBytes, _store.Files[row.StorageKey]);
        Assert.Equal(1, _db.SaveCount); // source and file row in one save
    }

    [Fact]
    public async Task A_failed_save_leaves_no_stored_file()
    {
        _db.FailNextSave = new DbUpdateException("simulated");

        await Assert.ThrowsAsync<DbUpdateException>(() => UploadPdfAsync(ProjectA));

        Assert.Empty(_store.Files);
    }

    [Fact]
    public async Task With_originals_switched_off_only_the_text_is_kept()
    {
        var created = await Service(enabled: false).CreateFromUploadAsync(
            ProjectA, "srs.pdf", "text", new OriginalDocumentUpload(PdfBytes, OriginalDocumentFormats.Pdf, null));

        Assert.Null(created.OriginalDocument);
        Assert.Empty(_store.Files);
        Assert.Empty(InMemoryAiReapDb.Rows(_db.RequirementSourceFiles));
    }

    [Fact]
    public async Task A_member_who_may_read_raw_sources_gets_the_verified_bytes()
    {
        var created = await UploadPdfAsync(ProjectA);

        foreach (var role in new[] { Roles.BusinessAnalyst, Roles.Reviewer, Roles.QA })
        {
            var result = await Service(role).GetOriginalAsync(created.Id);
            Assert.Equal(OriginalDocumentStatus.Found, result.Status);
            Assert.Equal(PdfBytes, result.Content);
            Assert.Equal("application/pdf", result.ContentType);
            Assert.Equal("srs.pdf", result.FileName);
        }
    }

    [Fact]
    public async Task Another_projects_source_is_reported_exactly_like_a_missing_one()
    {
        var other = await UploadPdfAsync(ProjectB); // the caller is a member of ProjectA only

        var tampered = await Service().GetOriginalAsync(other.Id);
        var unknown = await Service().GetOriginalAsync(Guid.NewGuid());

        Assert.Equal(new OriginalDocumentResult(OriginalDocumentStatus.SourceNotFound), tampered);
        Assert.Equal(tampered, unknown);
    }

    [Fact]
    public async Task A_developer_is_refused_the_original_as_the_raw_text()
    {
        var created = await UploadPdfAsync(ProjectA);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(Roles.Developer).GetOriginalAsync(created.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(Roles.Developer).GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task A_legacy_source_has_no_original_and_says_so()
    {
        var legacy = new RequirementSource { Id = Guid.NewGuid(), ProjectId = ProjectA, SourceType = RequirementSourceType.FileUpload, RawText = "old", OriginalFileName = "old.pdf" };
        _db.RequirementSources.Add(legacy);

        Assert.Equal(OriginalDocumentStatus.NotRetained, (await Service().GetOriginalAsync(legacy.Id)).Status);
        Assert.Null((await Service().GetByIdAsync(legacy.Id))!.OriginalDocument);
    }

    [Fact]
    public async Task A_missing_or_altered_file_is_unavailable_never_served()
    {
        var created = await UploadPdfAsync(ProjectA);
        var key = _store.Files.Keys.Single();

        _store.Files[key][3] ^= 0xFF; // altered on disk
        Assert.Equal(OriginalDocumentStatus.Unavailable, (await Service().GetOriginalAsync(created.Id)).Status);

        _store.Files.Remove(key); // gone
        var result = await Service().GetOriginalAsync(created.Id);
        Assert.Equal(OriginalDocumentStatus.Unavailable, result.Status);
        Assert.Null(result.Content);
    }
}
