using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Api.Controllers;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// Phase 1 / G2 — re-running a downstream generation stage with the same logical output creates no
// duplicate artifact; the created and skipped counts are reported; existing artifacts (reviewed,
// edited, rejected, or created before fingerprints) are never changed. Scripted AI responses through
// the real services over InMemoryAiReapDb: no AI provider, no SQL Server, no network.
//
// Matching is exact after normalization (case, punctuation, plural "s", route parameter names), not
// semantic: Paraphrased_output_is_saved_as_new_artifacts_because_matching_is_not_semantic documents it.
[Trait("Category", "Phase1")]
[Trait("Gap", "G2")]
public class DownstreamIdempotencyTests
{
    // ---- workspace ----------------------------------------------------------------------------

    public sealed class Workspace
    {
        public InMemoryAiReapDb Db { get; } = new();
        public Project Project { get; } = new() { Id = Guid.NewGuid(), Name = "G2 synthetic project", CreatedByUserId = "phase1-offline-user" };
        public RequirementSource Source { get; }
        public Artifact Fr1 { get; }
        public Artifact Fr2 { get; }

        public Workspace()
        {
            Source = new RequirementSource
            {
                Id = Guid.NewGuid(), ProjectId = Project.Id, SourceType = RequirementSourceType.Paste,
                RawText = GoldenFixture.Source(GoldenFixture.ClinicBooking), CreatedByUserId = "phase1-offline-user"
            };
            Db.Projects.Add(Project);
            Db.RequirementSources.Add(Source);
            Fr1 = AddArtifact(ArtifactType.FunctionalRequirement, "FR-001", "Book a consultation room", Fr());
            Fr2 = AddArtifact(ArtifactType.FunctionalRequirement, "FR-002", "Prevent overlapping bookings", Fr());
        }

        private static string Fr() => JsonSerializer.Serialize(new FunctionalRequirementPayload { Actor = "Receptionist", ExpectedResult = "Done" });

        public Artifact AddArtifact(ArtifactType type, string code, string title, string dataJson, ArtifactStatus status = ArtifactStatus.AiGenerated, Guid? sourceId = null, bool noSource = false)
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = Project.Id, ArtifactType = type, Code = code, Title = title, Status = status,
                Origin = ArtifactOrigin.Ai, RequirementSourceId = noSource ? null : sourceId ?? Source.Id, DataJson = dataJson,
                CurrentVersion = 1, CreatedByUserId = "phase1-offline-user", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            Db.Artifacts.Add(artifact);
            return artifact;
        }

        public List<Artifact> Of(ArtifactType type) => InMemoryAiReapDb.Rows(Db.Artifacts).Where(a => a.ArtifactType == type).ToList();
        public int Fingerprints(ArtifactType type) => InMemoryAiReapDb.Rows(Db.ArtifactFingerprints).Count(f => f.ArtifactType == type);
        public int Links => InMemoryAiReapDb.Rows(Db.ArtifactRelationships).Count;
        public int Executions => InMemoryAiReapDb.Rows(Db.AIExecutions).Count;
    }

    private static readonly OfflineCurrentUser User = new();
    private static readonly AllowAllProjectAccess Access = new();

    // ---- scripted responses ---------------------------------------------------------------------

    private static string Stories(params (string Title, string Persona, string Value)[] items) => new JsonObject
    {
        ["userStories"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = i.Title, ["persona"] = i.Persona, ["valueStatement"] = i.Value, ["priority"] = "High",
            ["relatedRequirementCodes"] = new JsonArray("FR-001")
        }).ToArray())
    }.ToJsonString();

    private static string Rules(params (string Title, string Statement)[] items) => new JsonObject
    {
        ["businessRules"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = i.Title, ["statement"] = i.Statement, ["relatedRequirementCodes"] = new JsonArray("FR-002")
        }).ToArray())
    }.ToJsonString();

    private static string Entities(params (string Name, string Field)[] items) => new JsonObject
    {
        ["dataEntities"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = i.Name,
            ["fields"] = new JsonArray(new JsonObject { ["name"] = i.Field, ["dataType"] = "string", ["required"] = true }),
            ["keys"] = new JsonArray("Id"), ["relationships"] = null, ["indexes"] = new JsonArray(),
            ["relatedRequirementCodes"] = new JsonArray("FR-001")
        }).ToArray())
    }.ToJsonString();

    private static string Endpoints(params (string Method, string Route, string Purpose)[] items) => new JsonObject
    {
        ["apiSpecifications"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = $"{i.Method} {i.Route}", ["purpose"] = i.Purpose, ["method"] = i.Method, ["route"] = i.Route,
            ["request"] = null, ["response"] = null, ["validation"] = null, ["authorization"] = null,
            ["relatedRequirementCodes"] = new JsonArray("FR-001")
        }).ToArray())
    }.ToJsonString();

    private static string Tasks(params (string Title, string Description)[] items) => new JsonObject
    {
        ["tasks"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = i.Title, ["description"] = i.Description, ["taskType"] = "Backend", ["priority"] = "High",
            ["dependencies"] = new JsonArray(), ["suggestedRole"] = null, ["estimate"] = null,
            ["relatedRequirementCodes"] = new JsonArray("FR-001")
        }).ToArray())
    }.ToJsonString();

    private static string Criteria(params (string Given, string When, string Then)[] items) => new JsonObject
    {
        ["acceptanceCriteria"] = new JsonArray(items.Select((i, n) => (JsonNode)new JsonObject
        {
            ["title"] = $"Criterion {n + 1}", ["given"] = i.Given, ["when"] = i.When, ["then"] = i.Then, ["kind"] = "positive"
        }).ToArray())
    }.ToJsonString();

    private static string TestCases(params (string Title, string Step, string Expected)[] items) => new JsonObject
    {
        ["testCases"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = i.Title, ["preconditions"] = "Signed in as a receptionist", ["steps"] = new JsonArray(i.Step),
            ["expectedResult"] = i.Expected, ["testKind"] = "positive"
        }).ToArray())
    }.ToJsonString();

    private static string Design(string title, string overview) => new JsonObject
    {
        ["title"] = title, ["architectureOverview"] = overview, ["modules"] = new JsonArray("Bookings", "Accounts")
    }.ToJsonString();

    // ---- stage runners --------------------------------------------------------------------------

    public sealed record Stage(string Name, ArtifactType Type, Func<Workspace, ScriptedAiChatClient, Task<IReadOnlyList<ArtifactResponse>>> Run, string TwoItems)
    {
        public override string ToString() => Name;
    }

    private static readonly Stage[] SourceStages =
    [
        new("UserStories", ArtifactType.UserStory,
            (w, ai) => new UserStoryService(ai, w.Db, User, Access, NullLogger<UserStoryService>.Instance).GenerateStoriesAsync(w.Source.Id),
            Stories(("Book a room", "Receptionist", "Patients are seen on time"), ("See my week", "Clinician", "I can plan my week"))),
        new("BusinessRules", ArtifactType.BusinessRule,
            (w, ai) => new BusinessRuleService(ai, w.Db, User, Access, NullLogger<BusinessRuleService>.Instance).GenerateAsync(w.Source.Id),
            Rules(("No overlapping bookings", "A room cannot be booked twice for overlapping slots."), ("Deposit for private rooms", "Private consultations require a deposit."))),
        new("DataEntities", ArtifactType.DataEntity,
            (w, ai) => new DatabaseDesignService(ai, w.Db, User, Access, NullLogger<DatabaseDesignService>.Instance).GenerateAsync(w.Source.Id),
            Entities(("Booking", "RoomId"), ("Room", "Name"))),
        new("ApiSpecifications", ArtifactType.ApiSpecification,
            (w, ai) => new ApiDesignService(ai, w.Db, User, Access, NullLogger<ApiDesignService>.Instance).GenerateAsync(w.Source.Id),
            Endpoints(("POST", "/api/bookings", "Create a booking"), ("GET", "/api/rooms/{roomId}", "Read a room"))),
        new("ImplementationTasks", ArtifactType.ImplementationTask,
            (w, ai) => new ImplementationPlanningService(ai, w.Db, User, Access, NullLogger<ImplementationPlanningService>.Instance).GenerateAsync(w.Source.Id),
            Tasks(("Create Booking entity", "Add the Booking table and model."), ("Create POST /api/bookings", "Add the booking endpoint."))),
    ];

    public static TheoryData<Stage> SourceScopedStages() { var d = new TheoryData<Stage>(); foreach (var s in SourceStages) d.Add(s); return d; }

    // ---- repeated generation ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SourceScopedStages))]
    public async Task Rerunning_a_stage_with_the_same_output_creates_nothing_and_reports_every_item_skipped(Stage stage)
    {
        var w = new Workspace();

        var first = await stage.Run(w, new ScriptedAiChatClient(stage.TwoItems));
        var linksAfterFirst = w.Links;
        var second = await stage.Run(w, new ScriptedAiChatClient(stage.TwoItems));

        Assert.Equal(2, first.Count);
        Assert.Equal(0, Assert.IsType<GeneratedArtifacts>(first).SkippedDuplicates);
        Assert.Empty(second);
        Assert.Equal(2, Assert.IsType<GeneratedArtifacts>(second).SkippedDuplicates);
        Assert.Equal(2, w.Of(stage.Type).Count);
        Assert.Equal(2, w.Fingerprints(stage.Type));
        Assert.Equal(linksAfterFirst, w.Links);
        // The second call is still audited: one AI execution row with no produced artifact.
        Assert.Single(InMemoryAiReapDb.Rows(w.Db.AIExecutions), e => e.ProducedArtifactId is null);
        // One save per run (ArtifactPersistence), as before.
        Assert.Equal(2, w.Db.SaveCount);
    }

    [Theory]
    [MemberData(nameof(SourceScopedStages))]
    public async Task A_rerun_with_one_new_item_creates_only_that_item(Stage stage)
    {
        var w = new Workspace();
        await stage.Run(w, new ScriptedAiChatClient(stage.TwoItems));

        var oneNew = stage.Type switch
        {
            ArtifactType.UserStory => Stories(("Book a room", "Receptionist", "Patients are seen on time"), ("Cancel a booking", "Receptionist", "Rooms are freed")),
            ArtifactType.BusinessRule => Rules(("No overlapping bookings", "A room cannot be booked twice for overlapping slots."), ("Cancellation window", "Bookings can be cancelled up to 24 hours ahead.")),
            ArtifactType.DataEntity => Entities(("Booking", "RoomId"), ("Clinician", "Name")),
            ArtifactType.ApiSpecification => Endpoints(("POST", "/api/bookings", "Create a booking"), ("DELETE", "/api/bookings/{id}", "Cancel a booking")),
            _ => Tasks(("Create Booking entity", "Add the Booking table and model."), ("Create cancel endpoint", "Add DELETE /api/bookings/{id}.")),
        };
        var second = Assert.IsType<GeneratedArtifacts>(await stage.Run(w, new ScriptedAiChatClient(oneNew)));

        Assert.Single(second);
        Assert.Equal(1, second.SkippedDuplicates);
        Assert.Equal(3, w.Of(stage.Type).Count);
        Assert.Equal(3, w.Of(stage.Type).Select(a => a.Code).Distinct().Count());
    }

    [Fact]
    public async Task The_same_item_twice_in_one_response_is_saved_once()
    {
        var w = new Workspace();
        var rule = ("No overlapping bookings", "A room cannot be booked twice for overlapping slots.");

        var result = Assert.IsType<GeneratedArtifacts>(await SourceStages[1].Run(w, new ScriptedAiChatClient(Rules(rule, rule))));

        Assert.Single(result);
        Assert.Equal(1, result.SkippedDuplicates);
    }

    [Fact]
    public async Task Matching_ignores_case_punctuation_plurals_and_route_parameter_names()
    {
        var w = new Workspace();
        await SourceStages[1].Run(w, new ScriptedAiChatClient(Rules(("No overlapping bookings", "A room cannot be booked twice for overlapping slots."))));
        await SourceStages[3].Run(w, new ScriptedAiChatClient(Endpoints(("GET", "/api/rooms/{roomId}", "Read a room"))));
        await SourceStages[2].Run(w, new ScriptedAiChatClient(Entities(("Booking", "RoomId"))));

        var rules = Assert.IsType<GeneratedArtifacts>(await SourceStages[1].Run(w, new ScriptedAiChatClient(
            Rules(("no overlapping booking", "A ROOM cannot be booked twice, for overlapping slots")))));
        var endpoints = Assert.IsType<GeneratedArtifacts>(await SourceStages[3].Run(w, new ScriptedAiChatClient(
            Endpoints(("get", "/API/Rooms/:id/", "Fetch one room by its identifier")))));
        // A data entity is one per name: the same entity with other fields is the same entity.
        var entities = Assert.IsType<GeneratedArtifacts>(await SourceStages[2].Run(w, new ScriptedAiChatClient(Entities(("booking", "ClinicianId")))));

        Assert.Equal((0, 1), (rules.Count, rules.SkippedDuplicates));
        Assert.Equal((0, 1), (endpoints.Count, endpoints.SkippedDuplicates));
        Assert.Equal((0, 1), (entities.Count, entities.SkippedDuplicates));
    }

    // Documented limitation: matching is exact after normalization, not semantic. A paraphrase of an
    // existing item is a new artifact.
    [Fact]
    public async Task Paraphrased_output_is_saved_as_new_artifacts_because_matching_is_not_semantic()
    {
        var w = new Workspace();
        await SourceStages[1].Run(w, new ScriptedAiChatClient(Rules(("No overlapping bookings", "A room cannot be booked twice for overlapping slots."))));

        var paraphrase = Assert.IsType<GeneratedArtifacts>(await SourceStages[1].Run(w, new ScriptedAiChatClient(
            Rules(("Prevent double booking", "Overlapping bookings of one room are not allowed.")))));

        Assert.Single(paraphrase);
        Assert.Equal(0, paraphrase.SkippedDuplicates);
        Assert.Equal(2, w.Of(ArtifactType.BusinessRule).Count);
    }

    // ---- parent-specific acceptance criteria and test cases -----------------------------------

    private static Task<IReadOnlyList<ArtifactResponse>?> RunCriteria(Workspace w, Artifact story, string response) =>
        new UserStoryService(new ScriptedAiChatClient(response), w.Db, User, Access, NullLogger<UserStoryService>.Instance)
            .GenerateAcceptanceCriteriaAsync(story.Id);

    private static Task<IReadOnlyList<ArtifactResponse>?> RunTests(Workspace w, Artifact fr, string response) =>
        new TestGenerationService(new ScriptedAiChatClient(response), w.Db, User, Access, NullLogger<TestGenerationService>.Instance)
            .GenerateAsync(fr.Id);

    private static string StoryJson() => JsonSerializer.Serialize(new UserStoryPayload { Persona = "Receptionist", ValueStatement = "Rooms are not double-booked" });

    [Fact]
    public async Task Acceptance_criteria_are_deduplicated_per_story_and_the_same_wording_under_another_story_is_kept()
    {
        var w = new Workspace();
        var storyA = w.AddArtifact(ArtifactType.UserStory, "US-001", "Book a room", StoryJson());
        var storyB = w.AddArtifact(ArtifactType.UserStory, "US-002", "Cancel a booking", StoryJson());
        var criteria = Criteria(("a free room", "the receptionist books it", "the booking is saved"), ("a booked room", "another booking overlaps", "it is refused"));

        var a1 = Assert.IsType<GeneratedArtifacts>(await RunCriteria(w, storyA, criteria));
        var b1 = Assert.IsType<GeneratedArtifacts>(await RunCriteria(w, storyB, criteria));
        var a2 = Assert.IsType<GeneratedArtifacts>(await RunCriteria(w, storyA, criteria));

        Assert.Equal((2, 0), (a1.Count, a1.SkippedDuplicates));
        Assert.Equal((2, 0), (b1.Count, b1.SkippedDuplicates)); // same wording, other story: kept
        Assert.Equal((0, 2), (a2.Count, a2.SkippedDuplicates));
        Assert.Equal(4, w.Of(ArtifactType.AcceptanceCriterion).Count);
        Assert.Equal(4, w.Fingerprints(ArtifactType.AcceptanceCriterion));
    }

    [Fact]
    public async Task Test_cases_are_deduplicated_per_requirement_and_the_same_wording_under_another_requirement_is_kept()
    {
        var w = new Workspace();
        var cases = TestCases(("Book a free room", "Book room 1 for 10:00", "The booking is saved"));

        var fr1 = Assert.IsType<GeneratedArtifacts>(await RunTests(w, w.Fr1, cases));
        var fr2 = Assert.IsType<GeneratedArtifacts>(await RunTests(w, w.Fr2, cases));
        var fr1Again = Assert.IsType<GeneratedArtifacts>(await RunTests(w, w.Fr1, cases));

        Assert.Equal((1, 0), (fr1.Count, fr1.SkippedDuplicates));
        Assert.Equal((1, 0), (fr2.Count, fr2.SkippedDuplicates));
        Assert.Equal((0, 1), (fr1Again.Count, fr1Again.SkippedDuplicates));
        Assert.Equal(2, w.Of(ArtifactType.TestCase).Count);
        Assert.Equal(2, InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships).Count(r => r.RelationshipType == RelationshipType.TestedBy));
    }

    [Fact]
    public async Task Test_cases_for_a_requirement_with_no_source_are_deduplicated_from_current_content_without_a_fingerprint_row()
    {
        var w = new Workspace();
        var humanFr = w.AddArtifact(ArtifactType.FunctionalRequirement, "FR-003", "Record cancellations",
            JsonSerializer.Serialize(new FunctionalRequirementPayload { Actor = "System" }), noSource: true);
        var cases = TestCases(("Cancellation is recorded", "Cancel booking 7", "Who and when are stored"));

        var first = Assert.IsType<GeneratedArtifacts>(await RunTests(w, humanFr, cases));
        var second = Assert.IsType<GeneratedArtifacts>(await RunTests(w, humanFr, cases));

        Assert.Equal((1, 0), (first.Count, first.SkippedDuplicates));
        Assert.Equal((0, 1), (second.Count, second.SkippedDuplicates));
        Assert.Equal(0, w.Fingerprints(ArtifactType.TestCase));
    }

    // ---- preservation of existing artifacts ---------------------------------------------------

    private static (string Title, string Status, string DataJson, int Version, DateTime Updated) Snapshot(Artifact a) =>
        (a.Title, a.Status.ToString(), a.DataJson, a.CurrentVersion, a.UpdatedAt);

    [Fact]
    public async Task Reviewed_and_human_edited_stories_are_never_changed_by_a_rerun()
    {
        var w = new Workspace();
        var original = Stories(("Book a room", "Receptionist", "Patients are seen on time"));
        var story = (await SourceStages[0].Run(w, new ScriptedAiChatClient(original))).Single();
        var saved = w.Of(ArtifactType.UserStory).Single(a => a.Id == story.Id);

        // A reviewer approved it, then a BA edited it (as ArtifactService would: new title, v2).
        saved.Status = ArtifactStatus.Approved;
        saved.Title = "Book a consultation room";
        saved.DataJson = JsonSerializer.Serialize(new UserStoryPayload { Persona = "Receptionist", ValueStatement = "Clinicians start on time" });
        saved.CurrentVersion = 2;
        var before = Snapshot(saved);
        var versionsBefore = InMemoryAiReapDb.Rows(w.Db.ArtifactVersions).Count(v => v.ArtifactId == saved.Id);

        // The AI's original wording (matched by the fingerprint recorded at creation) ...
        var rerunOriginal = Assert.IsType<GeneratedArtifacts>(await SourceStages[0].Run(w, new ScriptedAiChatClient(original)));
        // ... and the human-edited wording (matched by current content) are both skipped.
        var rerunEdited = Assert.IsType<GeneratedArtifacts>(await SourceStages[0].Run(w, new ScriptedAiChatClient(
            Stories(("Book a consultation room", "Receptionist", "Clinicians start on time")))));

        Assert.Equal((0, 1), (rerunOriginal.Count, rerunOriginal.SkippedDuplicates));
        Assert.Equal((0, 1), (rerunEdited.Count, rerunEdited.SkippedDuplicates));
        Assert.Single(w.Of(ArtifactType.UserStory));
        Assert.Equal(before, Snapshot(saved));
        Assert.Equal(versionsBefore, InMemoryAiReapDb.Rows(w.Db.ArtifactVersions).Count(v => v.ArtifactId == saved.Id));
    }

    [Fact]
    public async Task A_rejected_artifact_is_not_regenerated_and_stays_rejected()
    {
        var w = new Workspace();
        var rule = Rules(("Deposit for private rooms", "Private consultations require a deposit."));
        var created = (await SourceStages[1].Run(w, new ScriptedAiChatClient(rule))).Single();
        var saved = w.Of(ArtifactType.BusinessRule).Single(a => a.Id == created.Id);
        saved.Status = ArtifactStatus.Rejected;
        var before = Snapshot(saved);

        var rerun = Assert.IsType<GeneratedArtifacts>(await SourceStages[1].Run(w, new ScriptedAiChatClient(rule)));

        Assert.Equal((0, 1), (rerun.Count, rerun.SkippedDuplicates));
        Assert.Equal(before, Snapshot(saved));
    }

    [Fact]
    public async Task An_artifact_created_before_fingerprints_existed_is_matched_by_its_current_content()
    {
        var w = new Workspace();
        // No fingerprint row, as for every downstream artifact saved before G2.
        w.AddArtifact(ArtifactType.ApiSpecification, "API-001", "POST /api/bookings",
            JsonSerializer.Serialize(new ApiSpecificationPayload { Method = "POST", Route = "/api/bookings", Purpose = "Create a booking" }));

        var rerun = Assert.IsType<GeneratedArtifacts>(await SourceStages[3].Run(w, new ScriptedAiChatClient(
            Endpoints(("POST", "/api/bookings/", "Create a new booking")))));

        Assert.Equal((0, 1), (rerun.Count, rerun.SkippedDuplicates));
        Assert.Single(w.Of(ArtifactType.ApiSpecification));
        Assert.Equal(0, w.Fingerprints(ArtifactType.ApiSpecification));
    }

    // ---- solution design (one artifact per call) ----------------------------------------------

    [Fact]
    public async Task The_same_solution_design_is_returned_unchanged_and_not_saved_again()
    {
        var w = new Workspace();
        SolutionDesignService Service(string response) => new(new ScriptedAiChatClient(response), w.Db, User, Access, NullLogger<SolutionDesignService>.Instance);
        var design = Design("Booking platform", "A web API with a relational database behind it.");

        var (first, firstCreated) = await Service(design).GenerateWithOutcomeAsync(w.Source.Id);
        var (second, secondCreated) = await Service(design).GenerateWithOutcomeAsync(w.Source.Id);
        var (other, otherCreated) = await Service(Design("Booking platform", "An event-driven design with a message queue.")).GenerateWithOutcomeAsync(w.Source.Id);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(first.Id, second.Id);
        Assert.True(otherCreated);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal(2, w.Of(ArtifactType.DesignArtifact).Count);
        // GenerateAsync (agents) returns the existing design too.
        Assert.Equal(first.Id, (await Service(design).GenerateAsync(w.Source.Id)).Id);
    }

    [Fact]
    public async Task A_solution_design_service_without_deduplication_reports_every_design_as_created()
    {
        ISolutionDesignService legacy = new FixedDesign();

        var (design, created) = await legacy.GenerateWithOutcomeAsync(Guid.NewGuid());

        Assert.True(created);
        Assert.Equal("DA-001", design.Code);
    }

    private sealed class FixedDesign : ISolutionDesignService
    {
        public Task<ArtifactResponse> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ArtifactResponseMapper.ToResponse(new Artifact { Id = Guid.NewGuid(), Code = "DA-001", Title = "Design", DataJson = "{}" }));
    }

    // ---- reporting ----------------------------------------------------------------------------

    [Fact]
    public void Count_headers_are_written_for_a_generation_result_and_not_for_a_plain_list()
    {
        var generated = new DefaultHttpContext().Response;
        var plain = new DefaultHttpContext().Response;
        var created = new List<ArtifactResponse>
        {
            ArtifactResponseMapper.ToResponse(new Artifact { Id = Guid.NewGuid(), Code = "US-001", Title = "Story", DataJson = "{}" })
        };

        GenerationCountHeaders.Write(generated, new GeneratedArtifacts(created, 3));
        GenerationCountHeaders.Write(plain, created);

        Assert.Equal("1", generated.Headers[GenerationCountHeaders.Created].ToString());
        Assert.Equal("3", generated.Headers[GenerationCountHeaders.Skipped].ToString());
        Assert.False(plain.Headers.ContainsKey(GenerationCountHeaders.Created));
    }

    [Fact]
    public void A_generation_result_serializes_as_the_same_json_array_as_before()
    {
        var created = new List<ArtifactResponse>
        {
            ArtifactResponseMapper.ToResponse(new Artifact { Id = Guid.Empty, Code = "US-001", Title = "Story", DataJson = "{}" })
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal(JsonSerializer.Serialize<object>(created, options), JsonSerializer.Serialize<object>(new GeneratedArtifacts(created, 5), options));
    }
}
