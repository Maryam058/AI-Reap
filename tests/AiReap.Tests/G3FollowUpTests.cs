using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Application.Sdlc;
using AiReap.Application.Traceability;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace AiReap.Tests;

// G3 follow-ups: D1 (requirement data must be a JSON object), D2 (an override at submission covers
// failures by stable identity, not by label and detail), P2 (an FR/NFR's author may not approve it)
// and P3 (a derived/assumed origin needs a derivation note).
//
// The real services over InMemoryAiReapDb, as in EvidenceApprovalGateTests: no AI provider, no SQL
// Server, no network. LINQ runs as LINQ-to-objects, so EF translation is not covered here.
[Trait("Gap", "G3")]
public class G3FollowUpTests
{
    private const string SourceEvidenceLabel = "Requirement source evidence and NFR targets accepted";

    // ---- payloads -------------------------------------------------------------------------------

    private static EvidenceQuotePayload Quote(string status) =>
        new() { From = "srs", Quote = "the clerk records every booking", SourceId = "FR-001", Status = status, Reason = status == "verified" ? null : "not_found", MatchCount = status == "verified" ? 1 : 0 };

    private static string Fr(string? evidenceStatus = "verified", string? origin = "explicit", string? note = null) =>
        JsonSerializer.Serialize(new FunctionalRequirementPayload
        {
            Actor = "Clerk",
            ExpectedResult = "Booking stored.",
            RequirementOrigin = origin,
            DerivationNote = note,
            Evidence = evidenceStatus is null or "none" ? [] : [Quote(evidenceStatus)],
            EvidenceStatus = evidenceStatus,
            EvidenceCheck = RequirementEvidence.CheckVersion
        });

    private static string Nfr(string? evidenceStatus = "verified", string? origin = "explicit", string? note = null) =>
        JsonSerializer.Serialize(new NonFunctionalRequirementPayload
        {
            Category = "Performance",
            Description = "Search responds quickly.",
            RequirementOrigin = origin,
            DerivationNote = note,
            Evidence = evidenceStatus is null or "none" ? [] : [Quote(evidenceStatus)],
            EvidenceStatus = evidenceStatus,
            EvidenceCheck = RequirementEvidence.CheckVersion
        });

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // The artifact's current data with these changes applied (null value: key removed).
    private static JsonElement Edited(Artifact artifact, params (string Key, JsonNode? Value)[] changes)
    {
        var data = JsonNode.Parse(artifact.DataJson)!.AsObject();
        foreach (var (key, value) in changes)
        {
            foreach (var k in data.Select(kv => kv.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList()) data.Remove(k);
            if (value is not null) data[key] = value;
        }

        return Element(data.ToJsonString());
    }

    private static UpdateArtifactRequest Edit(JsonElement? data) => new(null, null, data, "G3 follow-up test edit");

    private static UpdateArtifactStatusRequest Approve() => new(ArtifactStatus.Approved, null);

    // ---- fakes ----------------------------------------------------------------------------------

    private sealed class User(string id, params string[] roles) : ICurrentUser
    {
        public string UserId => id;
        public bool IsInRole(string role) => roles.Contains(role);
    }

    private sealed class RoleDirectory(Dictionary<string, string[]> roles) : IUserRoleDirectory
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetActiveUserRolesAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(userIds.Distinct().Where(roles.ContainsKey)
                .ToDictionary(u => u, u => (IReadOnlyList<string>)roles[u]));

        public Task<IReadOnlyList<string>> GetActiveUsersInRoleAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(roles.Where(r => r.Value.Contains(role)).Select(r => r.Key).ToList());
    }

    private sealed class OpenAccess : IProjectAccessService
    {
        public Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoImpact : IImpactAnalysisService
    {
        public Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> StageNoticesForChangeAsync(Artifact changed, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForProjectAsync(Guid projectId, bool includeAcknowledged, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImpactNoticeResponse?> AcknowledgeAsync(Guid noticeId, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---- workspace: a project whose Design and Testing exit checks all pass ----------------------

    private sealed class Workspace
    {
        public static readonly User Dev = new("dev", Roles.Developer);
        public static readonly User Reviewer = new("rev", Roles.Reviewer);
        public static readonly User Qa = new("qa", Roles.QA);
        public static readonly User Admin = new("admin", Roles.Administrator);
        public static readonly User Admin2 = new("admin2", Roles.Administrator);
        public static readonly User Ba = new("ba", Roles.BusinessAnalyst);
        public static readonly User Ba2 = new("ba2", Roles.BusinessAnalyst);

        private readonly RoleDirectory _roles = new(new()
        {
            ["dev"] = [Roles.Developer], ["rev"] = [Roles.Reviewer], ["qa"] = [Roles.QA], ["admin"] = [Roles.Administrator],
            ["admin2"] = [Roles.Administrator], ["ba"] = [Roles.BusinessAnalyst], ["ba2"] = [Roles.BusinessAnalyst]
        });

        public InMemoryAiReapDb Db { get; } = new();
        public Project Project { get; }
        public Guid SourceId { get; } = Guid.NewGuid();
        public Artifact Fr { get; }

        public Workspace(ProjectStatus status, string? frJson = null)
        {
            Project = new Project { Id = Guid.NewGuid(), Name = "G3 follow-up offline", Status = status, Objectives = "o", BusinessProblem = "p", Scope = "s", CreatedByUserId = "ba" };
            Db.Projects.Add(Project);
            foreach (var user in new[] { Dev, Reviewer, Qa, Admin, Admin2, Ba, Ba2 })
            {
                Db.ProjectMembers.Add(new ProjectMember { Id = Guid.NewGuid(), ProjectId = Project.Id, UserId = user.UserId, AddedByUserId = "ba", AddedAt = DateTime.UtcNow });
            }

            Fr = Add(ArtifactType.FunctionalRequirement, "FR-001", frJson ?? Fr());
            var story = Add(ArtifactType.UserStory, "US-001");
            var criterion = Add(ArtifactType.AcceptanceCriterion, "AC-001");
            var design = Add(ArtifactType.DesignArtifact, "DES-001");
            var api = Add(ArtifactType.ApiSpecification, "API-001");
            var entity = Add(ArtifactType.DataEntity, "DE-001");
            var task = Add(ArtifactType.ImplementationTask, "TASK-001");
            var test = Add(ArtifactType.TestCase, "TC-001");

            Link(story, Fr, RelationshipType.DerivedFrom);
            Link(criterion, story, RelationshipType.DerivedFrom);
            Link(task, Fr, RelationshipType.Implements);
            Link(Fr, test, RelationshipType.TestedBy);
            foreach (var designed in new[] { design, api, entity })
            {
                Db.ArtifactReviews.Add(new ArtifactReview
                {
                    Id = Guid.NewGuid(), ArtifactId = designed.Id, ReviewerUserId = "dev", ReviewerRole = Roles.Developer,
                    Decision = ReviewDecision.CommentOnly, VersionNumber = designed.CurrentVersion, ReviewedAt = DateTime.UtcNow
                });
            }

            Work(task, WorkOutcome.TaskCompleted, Roles.Developer);
            Work(test, WorkOutcome.TestPassed, Roles.QA);
        }

        public Artifact Add(ArtifactType type, string code, string dataJson = "{}", ArtifactStatus status = ArtifactStatus.Approved)
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = Project.Id, ArtifactType = type, Code = code, Title = code, Status = status,
                Origin = ArtifactOrigin.Ai, RequirementSourceId = SourceId, DataJson = dataJson, CurrentVersion = 1,
                ApprovedVersion = ArtifactStatusTransitions.IsApprovedOrLater(status) ? 1 : null,
                CreatedByUserId = "ba", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            Db.Artifacts.Add(artifact);
            return artifact;
        }

        public ArtifactRelationship Link(Artifact source, Artifact target, RelationshipType type)
        {
            var relationship = new ArtifactRelationship { Id = Guid.NewGuid(), SourceArtifactId = source.Id, TargetArtifactId = target.Id, RelationshipType = type, CreatedAt = DateTime.UtcNow };
            Db.ArtifactRelationships.Add(relationship);
            return relationship;
        }

        public void Unlink(ArtifactRelationship relationship) => InMemoryAiReapDb.Rows(Db.ArtifactRelationships).Remove(relationship);

        public void Work(Artifact artifact, WorkOutcome outcome, string role) =>
            Db.ArtifactWorkRecords.Add(new ArtifactWorkRecord
            {
                Id = Guid.NewGuid(), ArtifactId = artifact.Id, VersionNumber = 1, Outcome = outcome, RecordedByUserId = role, RecordedByRole = role, RecordedAt = DateTime.UtcNow
            });

        public ProjectStageTransition Transition => InMemoryAiReapDb.Rows(Db.ProjectStageTransitions).Single();

        private LifecycleEvidenceService Evidence(User user) => new(Db, _roles, user);

        public ProjectLifecycleService Lifecycle(User user) =>
            new(Db, user, new OpenAccess(), Evidence(user), new LifecycleNotifications(Db, _roles), new ExitCheckGuidanceService(Db, _roles, user),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectLifecycleService>.Instance);

        public ArtifactService Artifacts(User user) => new(Db, user, new OpenAccess(), new NoImpact());

        // An Administrator's submission with an override reason, standing in for the stage owner.
        public Task<StageTransitionResponse> SubmitOverrideAsync(string reason) =>
            Lifecycle(Admin).AdvanceAsync(Project.Id, new AdvanceStageRequest(ProjectLifecyclePolicy.Next(Project.Status)!.Value, null, reason));

        public Task<StageTransitionResponse> DecideAsync(Guid transitionId, User user, string? overrideReason = null) =>
            Lifecycle(user).DecideAsync(transitionId, new DecideStageTransitionRequest(true, null, overrideReason));
    }

    private static object State(Artifact a) => (a.Status, a.DataJson, a.CurrentVersion, a.ApprovedVersion, a.Title, a.UpdatedAt, a.UpdatedByUserId);

    private static void AssertUntouched(Workspace w, Artifact artifact, object before)
    {
        Assert.Equal(before, State(artifact));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactReviews).Where(r => r.ArtifactId == artifact.Id));
        Assert.Equal(0, w.Db.SaveCount);
    }

    // ---- D1: requirement data must be a JSON object ---------------------------------------------

    public static TheoryData<ArtifactType, string> NonObjectData()
    {
        var data = new TheoryData<ArtifactType, string>();
        foreach (var type in new[] { ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement })
        foreach (var json in new[] { "[]", "\"x\"", "1", "true", "null" })
        {
            data.Add(type, json);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NonObjectData))]
    public async Task Requirement_data_that_is_not_an_object_is_refused_and_changes_nothing(ArtifactType type, string json)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(type, "REQ-060", type == ArtifactType.FunctionalRequirement ? Fr("unverified") : Nfr("unverified"), ArtifactStatus.AiGenerated);
        var before = State(artifact);

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id, Edit(Element(json))));

        Assert.Contains("must be a JSON object", refused.Message);
        AssertUntouched(w, artifact, before);

        // The stored evidence still decides approval: an evidence-checked record never turns unchecked.
        var assessment = RequirementApprovalEvidence.Assess(type, artifact.DataJson);
        Assert.False(assessment.EvidenceUnchecked);
        var blocked = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Workspace.Reviewer).UpdateStatusAsync(artifact.Id, Approve()));
        Assert.Contains(RequirementApprovalEvidence.EvidenceUnverified, blocked.Message);
        Assert.Equal(ArtifactStatus.AiGenerated, artifact.Status);
    }

    [Fact]
    public async Task Omitting_requirement_data_changes_nothing()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-060", Fr("unverified"), ArtifactStatus.AiGenerated);
        var before = State(artifact);

        var response = await w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id, Edit(null));

        Assert.Equal(1, response!.CurrentVersion);
        AssertUntouched(w, artifact, before);
    }

    [Theory]
    [InlineData(ArtifactType.FunctionalRequirement)]
    [InlineData(ArtifactType.NonFunctionalRequirement)]
    public async Task A_valid_object_edit_still_saves_and_keeps_the_server_owned_evidence(ArtifactType type)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(type, "REQ-061", type == ArtifactType.FunctionalRequirement ? Fr("unverified") : Nfr("unverified"), ArtifactStatus.AiGenerated);
        var field = type == ArtifactType.FunctionalRequirement ? "Actor" : "Description";

        await w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id, Edit(Edited(artifact, (field, "Edited by a human"), ("EvidenceStatus", "verified"))));

        Assert.Equal(2, artifact.CurrentVersion);
        var stored = JsonNode.Parse(artifact.DataJson)!.AsObject();
        Assert.Equal("Edited by a human", stored[field]!.GetValue<string>());
        Assert.Equal("unverified", stored["EvidenceStatus"]!.GetValue<string>()); // a forged status is not kept
        await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Workspace.Reviewer).UpdateStatusAsync(artifact.Id, Approve()));
    }

    // ---- P3: a derived/assumed origin needs a derivation note -------------------------------------

    [Theory]
    [InlineData("derived", null)]
    [InlineData("derived", "")]
    [InlineData("assumed", "   ")]
    [InlineData(" Assumed ", "\t\n")]
    public async Task Reclassifying_as_derived_or_assumed_without_a_note_is_refused(string origin, string? note)
    {
        foreach (var type in new[] { ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement })
        {
            var w = new Workspace(ProjectStatus.Analysis);
            var artifact = w.Add(type, "REQ-070", type == ArtifactType.FunctionalRequirement ? Fr("none") : Nfr("none"), ArtifactStatus.AiGenerated);
            var before = State(artifact);

            var refused = await Assert.ThrowsAsync<RequestValidationException>(() => w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id,
                Edit(Edited(artifact, ("RequirementOrigin", origin), ("DerivationNote", note)))));

            Assert.Contains("derivation note is required", refused.Message);
            AssertUntouched(w, artifact, before);
        }
    }

    [Theory]
    [InlineData(ArtifactType.FunctionalRequirement, "derived")]
    [InlineData(ArtifactType.NonFunctionalRequirement, "assumed")]
    public async Task Reclassifying_with_a_note_keeps_the_note_and_the_approved_policy(ArtifactType type, string origin)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(type, "REQ-071", type == ArtifactType.FunctionalRequirement ? Fr("none") : Nfr("none"), ArtifactStatus.AiGenerated);
        Assert.True(RequirementApprovalEvidence.Assess(type, artifact.DataJson).Blocks); // explicit without a quote

        const string note = "Follows from the booking rule in section 3.2; not written as a requirement there.";
        await w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id, Edit(Edited(artifact, ("RequirementOrigin", origin), ("DerivationNote", note))));

        var stored = JsonNode.Parse(artifact.DataJson)!.AsObject();
        Assert.Equal(note, stored["DerivationNote"]!.GetValue<string>());
        Assert.Equal(origin, stored["RequirementOrigin"]!.GetValue<string>());
        var assessment = RequirementApprovalEvidence.Assess(type, artifact.DataJson);
        Assert.False(assessment.Blocks);
        Assert.Equal(origin, assessment.OriginWithoutQuote);
        Assert.Equal(ArtifactStatus.Approved, (await w.Artifacts(Workspace.Reviewer).UpdateStatusAsync(artifact.Id, Approve()))!.Status);
    }

    [Fact]
    public async Task A_derivation_note_has_a_length_limit()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-072", Fr("none"), ArtifactStatus.AiGenerated);
        var before = State(artifact);
        var max = RequirementOriginEdit.MaxDerivationNoteLength;

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id,
            Edit(Edited(artifact, ("RequirementOrigin", "derived"), ("DerivationNote", new string('n', max + 1))))));
        Assert.Contains($"at most {max} characters", refused.Message);
        AssertUntouched(w, artifact, before);

        // Exactly the limit is accepted (surrounding whitespace is not counted).
        var atLimit = "  " + new string('n', max) + "  ";
        await w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id, Edit(Edited(artifact, ("RequirementOrigin", "derived"), ("DerivationNote", atLimit))));
        Assert.Equal(atLimit, JsonNode.Parse(artifact.DataJson)!["DerivationNote"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task An_edit_cannot_erase_an_existing_derivation_note(string? note)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-073", Fr("none", "derived", "Follows from FR-001."), ArtifactStatus.AiGenerated);
        var before = State(artifact);

        await Assert.ThrowsAsync<RequestValidationException>(() => w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id,
            Edit(Edited(artifact, ("Actor", "Receptionist"), ("DerivationNote", note)))));
        AssertUntouched(w, artifact, before);
    }

    [Fact]
    public async Task An_unchanged_derived_origin_that_never_had_a_note_can_still_be_edited()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-074", Fr("none", "derived"), ArtifactStatus.AiGenerated);

        await w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id, Edit(Edited(artifact, ("Actor", "Receptionist"))));

        Assert.Equal(2, artifact.CurrentVersion);
        Assert.Null(JsonNode.Parse(artifact.DataJson)!["DerivationNote"]); // nothing generated for it

        // Switching between derived and assumed is a reclassification, which needs a note.
        await Assert.ThrowsAsync<RequestValidationException>(() => w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id,
            Edit(Edited(artifact, ("RequirementOrigin", "assumed")))));
        Assert.Equal(2, artifact.CurrentVersion);
    }

    [Fact]
    public async Task A_malformed_origin_or_note_cannot_slip_past_the_check()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-075", Fr("none"), ArtifactStatus.AiGenerated);
        var before = State(artifact);
        var service = w.Artifacts(Workspace.Ba2);

        // The same key twice in different casing: which one a reader sees would depend on order.
        var twice = JsonNode.Parse(artifact.DataJson)!.AsObject();
        twice["RequirementOrigin"] = "derived";
        twice["requirementOrigin"] = "explicit";
        twice["DerivationNote"] = "";
        twice["derivationNote"] = "a note";
        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(artifact.Id, Edit(Element(twice.ToJsonString()))));

        // A note that is not a string.
        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(artifact.Id,
            Edit(Edited(artifact, ("RequirementOrigin", "derived"), ("DerivationNote", 42)))));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(artifact.Id,
            Edit(Edited(artifact, ("RequirementOrigin", "derived"), ("DerivationNote", new JsonArray("a note"))))));

        AssertUntouched(w, artifact, before);
    }

    // ---- P2: an FR/NFR's author may not approve it ------------------------------------------------

    [Theory]
    [InlineData(ArtifactType.FunctionalRequirement)]
    [InlineData(ArtifactType.NonFunctionalRequirement)]
    public async Task The_author_cannot_approve_their_own_requirement(ArtifactType type)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(type, "REQ-080", type == ArtifactType.FunctionalRequirement ? Fr() : Nfr(), ArtifactStatus.AiGenerated);
        var before = State(artifact);

        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Artifacts(Workspace.Ba).UpdateStatusAsync(artifact.Id, Approve()));

        Assert.Contains("you are its author", refused.Message);
        AssertUntouched(w, artifact, before);

        var approved = await w.Artifacts(Workspace.Ba2).UpdateStatusAsync(artifact.Id, Approve());
        Assert.Equal(ArtifactStatus.Approved, approved!.Status);
        Assert.Equal("ba2", InMemoryAiReapDb.Rows(w.Db.ArtifactReviews).Single(r => r.ArtifactId == artifact.Id).ReviewerUserId);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("rev")]
    public async Task Holding_an_approver_or_administrator_role_does_not_let_the_author_approve(string author)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-081", Fr(), ArtifactStatus.AiGenerated);
        artifact.CreatedByUserId = author;
        var authorUser = author == "admin" ? Workspace.Admin : Workspace.Reviewer;
        var other = author == "admin" ? Workspace.Reviewer : Workspace.Admin;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Artifacts(authorUser).UpdateStatusAsync(artifact.Id, Approve()));
        Assert.Equal(ArtifactStatus.Approved, (await w.Artifacts(other).UpdateStatusAsync(artifact.Id, Approve()))!.Status);
    }

    [Fact]
    public async Task A_later_editor_is_not_the_author()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-082", Fr(), ArtifactStatus.AiGenerated);
        await w.Artifacts(Workspace.Ba2).UpdateAsync(artifact.Id, new UpdateArtifactRequest("Renamed by ba2", null, Edited(artifact, ("Actor", "Receptionist")), null));
        Assert.Equal("ba2", artifact.UpdatedByUserId);
        Assert.Equal("ba", artifact.CreatedByUserId); // an edit never changes the author

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Artifacts(Workspace.Ba).UpdateStatusAsync(artifact.Id, Approve()));
        Assert.Equal(ArtifactStatus.Approved, (await w.Artifacts(Workspace.Ba2).UpdateStatusAsync(artifact.Id, Approve()))!.Status);
    }

    [Fact]
    public async Task The_author_may_still_reject_or_move_their_requirement_through_review()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.FunctionalRequirement, "FR-083", Fr(), ArtifactStatus.AiGenerated);
        var service = w.Artifacts(Workspace.Ba);

        Assert.Equal(ArtifactStatus.UnderReview, (await service.UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.UnderReview, null)))!.Status);
        Assert.Equal(ArtifactStatus.Rejected, (await service.UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Rejected, "Not in the SRS.")))!.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_requirement_without_a_recorded_author_is_not_silently_approved(string author)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var artifact = w.Add(ArtifactType.NonFunctionalRequirement, "NFR-084", Nfr(), ArtifactStatus.AiGenerated);
        artifact.CreatedByUserId = author;
        var before = State(artifact);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Workspace.Reviewer).UpdateStatusAsync(artifact.Id, Approve()));

        Assert.Contains("no recorded author", refused.Message);
        AssertUntouched(w, artifact, before);
    }

    [Fact]
    public async Task Other_artifact_types_are_not_affected_by_the_author_rule()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var story = w.Add(ArtifactType.UserStory, "US-085", "{}", ArtifactStatus.AiGenerated);

        Assert.Equal(ArtifactStatus.Approved, (await w.Artifacts(Workspace.Ba).UpdateStatusAsync(story.Id, Approve()))!.Status);
    }

    [Fact]
    public void Approval_stays_restricted_to_the_approver_roles()
    {
        var action = typeof(AiReap.Api.Controllers.ArtifactsController).GetMethod(nameof(AiReap.Api.Controllers.ArtifactsController.UpdateStatus))!;
        var roles = action.GetCustomAttribute<AuthorizeAttribute>()!.Roles!.Split(',', StringSplitOptions.TrimEntries);

        Assert.Equal(new[] { Roles.Administrator, Roles.BusinessAnalyst, Roles.Reviewer }.Order(), roles.Order());
    }

    // ---- D2: an override covers failures by stable identity ---------------------------------------

    private static SdlcCheck Failing(StageTransitionResponse response, string label) => Assert.Single(response.Evidence, c => c.Label == label && !c.Passed);

    [Fact]
    public async Task Same_count_but_a_different_conflict_pair_is_not_covered()
    {
        var w = new Workspace(ProjectStatus.Testing);
        var br1 = w.Add(ArtifactType.BusinessRule, "BR-001");
        var br2 = w.Add(ArtifactType.BusinessRule, "BR-002");
        var br3 = w.Add(ArtifactType.BusinessRule, "BR-003");
        var pair = w.Link(br1, br2, RelationshipType.ConflictsWith);
        var submitted = await w.SubmitOverrideAsync("BR-001/BR-002 conflict accepted for release 1.");
        var before = Failing(submitted, "Conflicts resolved");

        w.Unlink(pair);
        w.Link(br1, br3, RelationshipType.ConflictsWith);
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));

        var failing = Assert.Single(refused.FailingChecks);
        Assert.Equal("Conflicts resolved", failing.Label);
        Assert.Equal(before.Detail, failing.Detail); // the display text alone could not tell them apart
        Assert.Contains("did not cover", refused.Message);
        Assert.Equal(ProjectStatus.Testing, w.Project.Status);

        // Neither a Reviewer nor a Developer may authorize it; a fresh Administrator override may.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer, "Looks fine."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.DecideAsync(submitted.Id, Workspace.Dev, "Looks fine."));
        Assert.Equal(StageTransitionState.Pending, w.Transition.State);
        var decided = await w.DecideAsync(submitted.Id, Workspace.Admin2, "BR-001/BR-003 accepted too.");
        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.Equal("BR-001/BR-002 conflict accepted for release 1. | BR-001/BR-003 accepted too.", decided.OverrideReason);
    }

    [Fact]
    public async Task Same_count_but_a_different_test_case_is_not_covered()
    {
        var w = new Workspace(ProjectStatus.Testing);
        var tc2 = w.Add(ArtifactType.TestCase, "TC-002", status: ArtifactStatus.UnderReview);
        var tc3 = w.Add(ArtifactType.TestCase, "TC-003");
        w.Work(tc3, WorkOutcome.TestPassed, Roles.QA);
        var submitted = await w.SubmitOverrideAsync("TC-002 approval pending with the client.");
        var before = Failing(submitted, "Test cases approved");

        tc2.Status = ArtifactStatus.Approved;
        tc2.ApprovedVersion = 1;
        w.Work(tc2, WorkOutcome.TestPassed, Roles.QA);
        tc3.Status = ArtifactStatus.UnderReview;
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));

        var failing = Assert.Single(refused.FailingChecks);
        Assert.Equal("Test cases approved", failing.Label);
        Assert.Equal(before.Detail, failing.Detail);
    }

    [Fact]
    public async Task Same_displayed_text_but_a_different_source_evidence_blocker_is_not_covered()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var submitted = await w.SubmitOverrideAsync("FR-001 quote wording accepted.");
        var before = Failing(submitted, SourceEvidenceLabel);

        w.Fr.DataJson = Fr("none", origin: "explicit"); // same requirement, now no quote at all
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));

        var failing = Assert.Single(refused.FailingChecks);
        Assert.Equal(before.Detail, failing.Detail);
        Assert.Equal(BlockerReasons.EvidenceMissing, Assert.Single(failing.Guidance!.Blockers).Reason);
    }

    [Fact]
    public async Task A_change_hidden_by_a_truncated_listing_is_not_covered()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var frs = Enumerable.Range(2, 6).Select(i => w.Add(ArtifactType.FunctionalRequirement, $"FR-00{i}", Fr("unverified"))).ToList();
        var fr8 = w.Add(ArtifactType.FunctionalRequirement, "FR-008", Fr("verified"));
        var submitted = await w.SubmitOverrideAsync("Seven quote mismatches accepted.");
        var before = Failing(submitted, SourceEvidenceLabel);
        Assert.Contains("and 2 more", before.Detail);

        frs[^1].DataJson = Fr("verified");  // FR-007, beyond the five listed
        fr8.DataJson = Fr("unverified");
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));

        Assert.Equal(before.Detail, Assert.Single(refused.FailingChecks).Detail);
    }

    [Fact]
    public async Task A_removed_blocker_plus_a_new_one_is_not_covered_but_a_removed_one_alone_is()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var fr2 = w.Add(ArtifactType.FunctionalRequirement, "FR-002", Fr("unverified"));
        var fr3 = w.Add(ArtifactType.FunctionalRequirement, "FR-003", Fr("verified"));
        var submitted = await w.SubmitOverrideAsync("FR-001 and FR-002 accepted.");

        fr2.DataJson = Fr("verified");
        fr3.DataJson = Fr("unverified");
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        Assert.Contains("FR-003", Assert.Single(refused.FailingChecks).Detail);

        // Only FR-001 left failing: a failure the override was given over.
        fr3.DataJson = Fr("verified");
        var decided = await w.DecideAsync(submitted.Id, Workspace.Reviewer);
        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.Equal("FR-001 and FR-002 accepted.", decided.OverrideReason);
    }

    [Fact]
    public async Task An_unchanged_failure_set_stays_covered_whatever_the_order()
    {
        var w = new Workspace(ProjectStatus.Testing, Fr("unverified"));
        w.Add(ArtifactType.FunctionalRequirement, "FR-002", Fr("unverified"));
        var br1 = w.Add(ArtifactType.BusinessRule, "BR-001");
        var br2 = w.Add(ArtifactType.BusinessRule, "BR-002");
        var br3 = w.Add(ArtifactType.BusinessRule, "BR-003");
        w.Link(br1, br2, RelationshipType.ConflictsWith);
        w.Link(br1, br3, RelationshipType.DuplicateOf);
        var submitted = await w.SubmitOverrideAsync("Accepted for release 1.");

        InMemoryAiReapDb.Rows(w.Db.Artifacts).Reverse();
        InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships).Reverse();
        var decided = await w.DecideAsync(submitted.Id, Workspace.Reviewer);

        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.Equal("Accepted for release 1.", decided.OverrideReason);
    }

    [Fact]
    public async Task The_history_still_shows_only_label_pass_and_detail()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var submitted = await w.SubmitOverrideAsync("Accepted.");

        Assert.Contains("\"failures\"", w.Transition.EvidenceSnapshotJson); // stored for the decision...
        var json = JsonSerializer.Serialize(submitted);
        Assert.DoesNotContain(w.Fr.Id.ToString("N"), json);                 // ...never returned
        Assert.DoesNotContain("failures", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(submitted.Evidence, (await w.Lifecycle(Workspace.Dev).GetHistoryAsync(w.Project.Id)).Single().Evidence);
    }

    public static TheoryData<string> UnusableSnapshots => new()
    {
        "not json",
        "{}",
        "[null]",
        "[{\"label\":\"x\",\"passed\":false,\"detail\":\"d\",\"key\":\"requirements.source-evidence\",\"failures\":[1]}]",
        "[{\"label\":\"x\",\"passed\":false,\"detail\":\"d\",\"key\":\"requirements.source-evidence\",\"failures\":[\"\"]}]",
    };

    [Theory]
    [MemberData(nameof(UnusableSnapshots))]
    public async Task An_unreadable_submission_snapshot_authorizes_nothing(string snapshot)
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var submitted = await w.SubmitOverrideAsync("Accepted.");
        w.Transition.EvidenceSnapshotJson = snapshot;

        await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        Assert.Equal(StageTransitionState.Pending, w.Transition.State);
    }

    [Fact]
    public async Task A_snapshot_from_before_failure_identities_authorizes_nothing()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var submitted = await w.SubmitOverrideAsync("Accepted.");
        // The earlier format: label, passed and detail only - identical text, no identities.
        w.Transition.EvidenceSnapshotJson = JsonSerializer.Serialize(submitted.Evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        var decided = await w.DecideAsync(submitted.Id, Workspace.Admin2, "Re-authorized after the upgrade.");
        Assert.Equal(StageTransitionState.Completed, decided.State);
    }

    // ---- D2: OverrideScope, directly ---------------------------------------------------------------

    private static Artifact A(string code, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), Code = code };

    private static CheckEvidence Check(string key, params CheckBlocker[] blockers) => new(new SdlcCheck("Label", false, "Detail"), key, blockers);

    [Fact]
    public void Failures_that_cannot_be_identified_are_never_covered()
    {
        var fr = A("FR-1");
        var good = Check("k", new CheckBlocker("r", [new BlockerArtifact(fr)]));
        var authorized = OverrideScope.FailureIds(good)!;

        Assert.Null(OverrideScope.FailureIds(Check("", new CheckBlocker("r", [new BlockerArtifact(fr)]))));
        Assert.Null(OverrideScope.FailureIds(Check("k")));
        Assert.Null(OverrideScope.FailureIds(Check("k", new CheckBlocker("", [new BlockerArtifact(fr)]))));
        Assert.Null(OverrideScope.FailureIds(Check("k", new CheckBlocker("r", [new BlockerArtifact(A("FR-0", Guid.Empty))]))));
        Assert.Null(OverrideScope.FailureIds(Check("k", new CheckBlocker("r", [new BlockerArtifact(fr, null, [A("X", Guid.Empty)])]))));
        Assert.False(OverrideScope.Covers(authorized, Check("k")));
        Assert.False(OverrideScope.Covers(new HashSet<string>(), good));
        Assert.True(OverrideScope.Covers(authorized, good));
    }

    [Fact]
    public void Identities_ignore_order_and_duplicates_but_not_artifacts_reasons_or_notes()
    {
        var (a, b, c) = (A("A"), A("B"), A("C"));
        var one = Check("k", new CheckBlocker("r", [new BlockerArtifact(a, "n", [b, c]), new BlockerArtifact(b)]));
        var reordered = Check("k", new CheckBlocker("r", [new BlockerArtifact(b), new BlockerArtifact(a, " n ", [c, b, c])]));

        Assert.Equal(OverrideScope.FailureIds(one)!.Order(), OverrideScope.FailureIds(reordered)!.Order());
        Assert.True(OverrideScope.Covers(OverrideScope.FailureIds(one)!, reordered));

        var authorized = OverrideScope.FailureIds(one)!;
        Assert.False(OverrideScope.Covers(authorized, Check("k", new CheckBlocker("r", [new BlockerArtifact(a, "n", [b])]))));        // related changed
        Assert.False(OverrideScope.Covers(authorized, Check("k", new CheckBlocker("other", [new BlockerArtifact(b)]))));               // reason changed
        Assert.False(OverrideScope.Covers(authorized, Check("k2", new CheckBlocker("r", [new BlockerArtifact(b)]))));                 // check changed
        Assert.False(OverrideScope.Covers(authorized, Check("k", new CheckBlocker("r", [new BlockerArtifact(a, "other note", [b, c])])))); // note changed
        Assert.True(OverrideScope.Covers(authorized, Check("k", new CheckBlocker("r", [new BlockerArtifact(b)]))));                   // a subset
    }

    [Fact]
    public void A_snapshot_reads_back_as_plain_checks_and_authorizes_only_its_failures()
    {
        var fr = A("FR-1");
        var checks = new[]
        {
            new CheckEvidence(new SdlcCheck("Passing", true, "ok"), "k1", []),
            Check("k2", new CheckBlocker("r", [new BlockerArtifact(fr)]), CheckBlocker.Of("not-run")),
        };

        var snapshot = OverrideScope.Snapshot(checks);

        Assert.Equal(checks.Select(c => c.Check), JsonSerializer.Deserialize<List<SdlcCheck>>(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(OverrideScope.FailureIds(checks[1])!.Order(), OverrideScope.Authorized(snapshot).Order());
        Assert.Equal(snapshot, OverrideScope.Snapshot(checks.ToList())); // deterministic
    }
}
