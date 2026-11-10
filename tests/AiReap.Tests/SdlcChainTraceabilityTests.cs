using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Traceability;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Workspace = AiReap.Tests.DownstreamIdempotencyTests.Workspace;

namespace AiReap.Tests;

// The SDLC chain end to end, offline: requirements -> stories -> acceptance criteria -> business rules ->
// design -> data entities -> APIs -> tasks -> FR and NFR test cases, through the real services with
// scripted AI responses (no provider call, no SQL Server). Checks that nothing generated is lost or left
// disconnected (no orphan, every NFR traced, every artifact audited), that running the whole chain again
// adds no duplicates, and that output the AI could not link is reported and can be linked by hand.
public class SdlcChainTraceabilityTests
{
    private static readonly OfflineCurrentUser User = new();
    private static readonly AllowAllProjectAccess Access = new();

    private static JsonArray Codes(params string[] codes) => new(codes.Select(c => (JsonNode)c).ToArray());

    private static string Stories() => new JsonObject
    {
        ["userStories"] = new JsonArray(
            new JsonObject { ["title"] = "Book a room", ["persona"] = "Receptionist", ["valueStatement"] = "Patients are seen on time", ["priority"] = "High", ["relatedRequirementCodes"] = Codes("FR-001") },
            new JsonObject { ["title"] = "Avoid clashes", ["persona"] = "Receptionist", ["valueStatement"] = "No room is double-booked", ["priority"] = "High", ["relatedRequirementCodes"] = Codes("FR-002") })
    }.ToJsonString();

    private static string Criteria(string what) => new JsonObject
    {
        ["acceptanceCriteria"] = new JsonArray(new JsonObject { ["title"] = $"{what} works", ["given"] = "a free room", ["when"] = what, ["then"] = "it is saved", ["kind"] = "positive" })
    }.ToJsonString();

    private static string Rules(params string[] codes) => new JsonObject
    {
        ["businessRules"] = new JsonArray(new JsonObject { ["title"] = "No overlapping bookings", ["statement"] = "A room cannot be booked twice for overlapping slots.", ["relatedRequirementCodes"] = Codes(codes) })
    }.ToJsonString();

    private static string Design() => new JsonObject { ["title"] = "Booking service", ["architectureOverview"] = "One web API and a SQL database.", ["modules"] = new JsonArray("Bookings") }.ToJsonString();

    private static string Entities() => new JsonObject
    {
        ["dataEntities"] = new JsonArray(new JsonObject
        {
            ["title"] = "Booking", ["fields"] = new JsonArray(new JsonObject { ["name"] = "RoomId", ["dataType"] = "string", ["required"] = true }),
            ["keys"] = new JsonArray("Id"), ["relationships"] = null, ["indexes"] = new JsonArray(), ["relatedRequirementCodes"] = Codes("FR-001", "FR-002")
        })
    }.ToJsonString();

    private static string Endpoints() => new JsonObject
    {
        ["apiSpecifications"] = new JsonArray(new JsonObject
        {
            ["title"] = "POST /api/bookings", ["purpose"] = "Create a booking", ["method"] = "POST", ["route"] = "/api/bookings", ["request"] = null,
            ["response"] = null, ["validation"] = null, ["authorization"] = null, ["relatedRequirementCodes"] = Codes("FR-001")
        })
    }.ToJsonString();

    private static string Tasks() => new JsonObject
    {
        ["tasks"] = new JsonArray(
            new JsonObject { ["title"] = "Create booking endpoint", ["description"] = "POST /api/bookings.", ["taskType"] = "Backend", ["priority"] = "High", ["dependencies"] = new JsonArray(), ["suggestedRole"] = null, ["estimate"] = null, ["relatedRequirementCodes"] = Codes("FR-001") },
            new JsonObject { ["title"] = "Reject overlapping slots", ["description"] = "Validate slot overlap.", ["taskType"] = "Backend", ["priority"] = "High", ["dependencies"] = new JsonArray(), ["suggestedRole"] = null, ["estimate"] = null, ["relatedRequirementCodes"] = Codes("FR-002") })
    }.ToJsonString();

    private static string Tests(string what, string kind = "positive") => new JsonObject
    {
        ["testCases"] = new JsonArray(new JsonObject { ["title"] = $"Test {what}", ["preconditions"] = null, ["steps"] = new JsonArray($"Do {what}"), ["expectedResult"] = "As specified", ["testKind"] = kind })
    }.ToJsonString();

    private static async Task RunChainAsync(Workspace w, Artifact nfr)
    {
        var stories = new UserStoryService(new ScriptedAiChatClient(Stories()), w.Db, User, Access, NullLogger<UserStoryService>.Instance);
        await stories.GenerateStoriesAsync(w.Source.Id);
        foreach (var story in w.Of(ArtifactType.UserStory))
        {
            await new UserStoryService(new ScriptedAiChatClient(Criteria(story.Title)), w.Db, User, Access, NullLogger<UserStoryService>.Instance)
                .GenerateAcceptanceCriteriaAsync(story.Id);
        }

        await new BusinessRuleService(new ScriptedAiChatClient(Rules("FR-002")), w.Db, User, Access, NullLogger<BusinessRuleService>.Instance).GenerateAsync(w.Source.Id);
        await new SolutionDesignService(new ScriptedAiChatClient(Design()), w.Db, User, Access, NullLogger<SolutionDesignService>.Instance).GenerateAsync(w.Source.Id);
        await new DatabaseDesignService(new ScriptedAiChatClient(Entities()), w.Db, User, Access, NullLogger<DatabaseDesignService>.Instance).GenerateAsync(w.Source.Id);
        await new ApiDesignService(new ScriptedAiChatClient(Endpoints()), w.Db, User, Access, NullLogger<ApiDesignService>.Instance).GenerateAsync(w.Source.Id);
        await new ImplementationPlanningService(new ScriptedAiChatClient(Tasks()), w.Db, User, Access, NullLogger<ImplementationPlanningService>.Instance).GenerateAsync(w.Source.Id);
        foreach (var requirement in new[] { w.Fr1, w.Fr2, nfr })
        {
            await new TestGenerationService(
                    new ScriptedAiChatClient(Tests(requirement.Code, requirement == nfr ? "performance" : "positive")), w.Db, User, Access,
                    NullLogger<TestGenerationService>.Instance)
                .GenerateAsync(requirement.Id);
        }
    }

    private static Artifact AddNfr(Workspace w) => w.AddArtifact(ArtifactType.NonFunctionalRequirement, "NFR-001", "Bookings confirm quickly",
        JsonSerializer.Serialize(new NonFunctionalRequirementPayload { Category = "Performance", Description = "Confirm a booking quickly." }));

    private static TraceabilityService Traceability(Workspace w) => new(w.Db, Access, User);

    private static Dictionary<ArtifactType, int> Counts(Workspace w) =>
        InMemoryAiReapDb.Rows(w.Db.Artifacts).GroupBy(a => a.ArtifactType).ToDictionary(g => g.Key, g => g.Count());

    [Fact]
    public async Task The_whole_chain_leaves_every_artifact_traced_audited_and_unduplicated()
    {
        var w = new Workspace();
        var nfr = AddNfr(w);

        await RunChainAsync(w, nfr);

        var counts = Counts(w);
        Assert.Equal(2, counts[ArtifactType.UserStory]);
        Assert.Equal(2, counts[ArtifactType.AcceptanceCriterion]);
        Assert.Equal(1, counts[ArtifactType.BusinessRule]);
        Assert.Equal(1, counts[ArtifactType.DesignArtifact]);
        Assert.Equal(1, counts[ArtifactType.DataEntity]);
        Assert.Equal(1, counts[ArtifactType.ApiSpecification]);
        Assert.Equal(2, counts[ArtifactType.ImplementationTask]);
        Assert.Equal(3, counts[ArtifactType.TestCase]);

        // Nothing disconnected: no orphan, the NFR is traced by its test case.
        var gaps = await Traceability(w).GetGapsAsync(w.Project.Id);
        Assert.Empty(gaps.Orphans);
        Assert.Empty(gaps.UntracedNonFunctionalRequirements);
        Assert.Single(Assert.Single(gaps.NonFunctionalTestCoverage).TestCases);

        // Each FR row reaches stories, criteria, design, data, API, tasks and tests.
        var matrix = await Traceability(w).GetMatrixAsync(w.Project.Id);
        Assert.All(matrix, row =>
        {
            Assert.NotEmpty(row.UserStories);
            Assert.NotEmpty(row.AcceptanceCriteria);
            Assert.NotEmpty(row.DesignArtifacts);
            Assert.NotEmpty(row.DataEntities);
            Assert.NotEmpty(row.ImplementationTasks);
            Assert.NotEmpty(row.TestCases);
        });
        Assert.Single(matrix.Single(r => r.FunctionalRequirementCode == "FR-002").BusinessRules);

        // Every generated artifact has the AI execution that produced it (§27).
        var produced = InMemoryAiReapDb.Rows(w.Db.AIExecutions).Select(e => e.ProducedArtifactId).OfType<Guid>().ToHashSet();
        Assert.All(InMemoryAiReapDb.Rows(w.Db.Artifacts).Where(a => a.Origin == ArtifactOrigin.Ai && a.CreatedByUserId == User.UserId),
            a => Assert.Contains(a.Id, produced));
    }

    [Fact]
    public async Task Running_the_whole_chain_again_with_the_same_output_adds_nothing()
    {
        var w = new Workspace();
        var nfr = AddNfr(w);
        await RunChainAsync(w, nfr);
        var before = Counts(w);
        var links = w.Links;

        await RunChainAsync(w, nfr);

        Assert.Equal(before, Counts(w));
        Assert.Equal(links, w.Links);
    }

    [Fact]
    public async Task A_rule_the_ai_could_not_link_is_kept_reported_and_can_be_linked_by_hand()
    {
        var w = new Workspace();
        await new BusinessRuleService(new ScriptedAiChatClient(Rules()), w.Db, User, Access, NullLogger<BusinessRuleService>.Instance).GenerateAsync(w.Source.Id);
        var rule = Assert.Single(w.Of(ArtifactType.BusinessRule)); // the useful output is kept

        var orphan = Assert.Single((await Traceability(w).GetGapsAsync(w.Project.Id)).Orphans);
        Assert.Equal((rule.Code, "requirement"), (orphan.Code, orphan.MissingLink));

        await Traceability(w).LinkToRequirementAsync(rule.Id, w.Fr2.Id);

        Assert.Empty((await Traceability(w).GetGapsAsync(w.Project.Id)).Orphans);
        Assert.Single((await Traceability(w).GetMatrixAsync(w.Project.Id)).Single(r => r.FunctionalRequirementId == w.Fr2.Id).BusinessRules);
    }
}
