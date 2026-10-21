using System.Text.Json;
using AiReap.Api.ErrorHandling;
using AiReap.Application.Agents;
using AiReap.Application.Agents.Implementations;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// A4a — a rejected artifact is never an input to new AI generation: it is left out of every prompt and
// of the codes the model may cite (so no link is made to it), the QA and Architecture agents skip or
// stop on it, and a direct request to build on one is refused with 409. Unit-level: the real services
// and agents run over InMemoryAiReapDb with a scripted AI client - no SQL Server, no AI provider.
public class GenerationInputIntegrityTests
{
    private const string RejectedTitle = "Delete approved leave";

    // ---- the rule itself ------------------------------------------------------------------------

    [Theory]
    [InlineData(ArtifactStatus.AiGenerated, true)]
    [InlineData(ArtifactStatus.Draft, true)]
    [InlineData(ArtifactStatus.UnderReview, true)]
    [InlineData(ArtifactStatus.Approved, true)]
    [InlineData(ArtifactStatus.Implemented, true)]
    [InlineData(ArtifactStatus.Verified, true)]
    [InlineData(ArtifactStatus.Rejected, false)]
    public void Every_status_but_rejected_is_a_generation_input(ArtifactStatus status, bool eligible)
    {
        Assert.Equal(eligible, GenerationInputs.IsEligible(new Artifact { Status = status }));
        Assert.Equal(7, Enum.GetValues<ArtifactStatus>().Length); // a new status must be decided here too
    }

    [Fact]
    public void Generation_inputs_leave_out_rejected_artifacts_and_keep_the_source_type_and_code_order()
    {
        var w = new World();
        var other = Guid.NewGuid();
        w.Add(ArtifactType.FunctionalRequirement, "FR-003", ArtifactStatus.Draft);
        w.Add(ArtifactType.FunctionalRequirement, "FR-002", ArtifactStatus.Rejected);
        w.Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Approved);
        w.Add(ArtifactType.NonFunctionalRequirement, "NFR-001", ArtifactStatus.Approved);
        w.Db.Artifacts.Add(new Artifact { Id = Guid.NewGuid(), Code = "FR-009", ArtifactType = ArtifactType.FunctionalRequirement, RequirementSourceId = other });

        var codes = GenerationInputs.For(InMemoryAiReapDb.Rows(w.Db.Artifacts).AsQueryable(), w.Source.Id, ArtifactType.FunctionalRequirement)
            .Select(a => a.Code).ToList();

        Assert.Equal(new[] { "FR-001", "FR-003" }, codes);
    }

    // ---- prompts and allowed codes --------------------------------------------------------------

    public static TheoryData<string> PromptStages => new()
    {
        "UserStoryGeneration", "BusinessRuleExtraction", "SolutionDesignGeneration",
        "DatabaseDesignGeneration", "ApiDesignGeneration", "ImplementationPlanning"
    };

    [Theory]
    [MemberData(nameof(PromptStages))]
    public async Task Rejected_requirements_are_left_out_of_every_generation_prompt_and_eligible_ones_stay(string stage)
    {
        var w = World.WithRequirements();
        w.Add(ArtifactType.DataEntity, "DE-001", ArtifactStatus.Approved, "Leave request entity");
        w.Add(ArtifactType.DataEntity, "DE-002", ArtifactStatus.Rejected, "Deleted leave archive");
        w.Add(ArtifactType.ApiSpecification, "API-001", ArtifactStatus.UnderReview, "Submit leave endpoint");
        w.Add(ArtifactType.ApiSpecification, "API-002", ArtifactStatus.Rejected, "Delete leave endpoint");
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, w, ai));

        var prompt = Assert.Single(ai.UserPrompts);
        Assert.Contains("FR-001: Submit leave request", prompt);
        Assert.Contains("FR-003: View leave balance", prompt);
        Assert.Contains("FR-004: Cancel leave request", prompt);
        Assert.DoesNotContain("FR-002", prompt);
        Assert.DoesNotContain(RejectedTitle, prompt);
        if (stage == "ImplementationPlanning")
        {
            Assert.Contains("DE-001: Leave request entity", prompt);
            Assert.Contains("API-001: Submit leave endpoint", prompt);
            Assert.DoesNotContain("DE-002", prompt);
            Assert.DoesNotContain("API-002", prompt);
        }
    }

    public static TheoryData<string, string> CitingStages => new()
    {
        { "UserStoryGeneration", """{"userStories":[{"title":"Delete leave","persona":"Manager","valueStatement":"tidy records","priority":"High","relatedRequirementCodes":["FR-002"]}]}""" },
        { "BusinessRuleExtraction", """{"businessRules":[{"title":"Deletion window","statement":"Approved leave may be deleted within 7 days.","relatedRequirementCodes":["FR-002"]}]}""" },
    };

    [Theory]
    [MemberData(nameof(CitingStages))]
    public async Task A_response_citing_a_rejected_requirement_is_refused_and_nothing_is_saved(string stage, string response)
    {
        var w = World.WithRequirements();
        var ai = new ScriptedAi(response, response); // the first answer and its one repair attempt
        var artifactsBefore = InMemoryAiReapDb.Rows(w.Db.Artifacts).Count;

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => Run(stage, w, ai));

        Assert.Contains(ex.Errors, e => e.Contains("'FR-002'") && e.Contains("not one of the artifacts provided"));
        Assert.Equal(0, w.Db.SaveCount);
        Assert.Equal(artifactsBefore, InMemoryAiReapDb.Rows(w.Db.Artifacts).Count);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
    }

    [Fact]
    public async Task Generated_stories_link_only_to_the_eligible_requirements_they_cite()
    {
        var w = World.WithRequirements();
        var ai = new ScriptedAi(
            """{"userStories":[{"title":"Request leave","persona":"Employee","valueStatement":"plan time off","priority":"High","relatedRequirementCodes":["FR-001","FR-003"]}]}""");

        var stories = await Run("UserStoryGeneration", w, ai);

        var story = Assert.Single(stories!);
        var links = InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships);
        Assert.All(links, l => Assert.Equal(story.Id, l.SourceArtifactId));
        Assert.All(links, l => Assert.Equal(RelationshipType.DerivedFrom, l.RelationshipType));
        Assert.Equal(
            new[] { w.Code("FR-001").Id, w.Code("FR-003").Id }.Order(),
            links.Select(l => l.TargetArtifactId).Order());
        Assert.Equal(1, w.Db.SaveCount);
    }

    // ---- direct requests ------------------------------------------------------------------------

    [Fact]
    public async Task Test_cases_for_a_rejected_requirement_are_refused_with_a_conflict_before_any_ai_call()
    {
        var w = World.WithRequirements();
        var ai = new ScriptedAi();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => w.Tests(ai).GenerateAsync(w.Code("FR-002").Id));

        Assert.StartsWith("FR-002 has been rejected", ex.Message);
        Assert.Empty(ai.UserPrompts);
        Assert.Equal(0, w.Db.SaveCount);
    }

    [Theory]
    [InlineData("FR-001")] // Approved
    [InlineData("FR-003")] // AiGenerated
    [InlineData("FR-004")] // Draft
    public async Task Test_cases_for_an_eligible_requirement_are_generated_as_before(string code)
    {
        var w = World.WithRequirements();
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Tests(ai).GenerateAsync(w.Code(code).Id));

        Assert.StartsWith($"{code}: ", Assert.Single(ai.UserPrompts));
    }

    [Fact]
    public async Task Acceptance_criteria_for_a_rejected_story_are_refused_with_a_conflict()
    {
        var w = World.WithRequirements();
        var story = w.Add(ArtifactType.UserStory, "US-001", ArtifactStatus.Rejected, "Request leave");
        w.Link(story, w.Code("FR-001"));
        var ai = new ScriptedAi();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id));

        Assert.StartsWith("US-001 has been rejected", ex.Message);
        Assert.Empty(ai.UserPrompts);
    }

    [Fact]
    public async Task Acceptance_criteria_for_a_story_whose_linked_requirements_are_all_rejected_are_refused()
    {
        var w = World.WithRequirements();
        var otherRejected = w.Add(ArtifactType.FunctionalRequirement, "FR-005", ArtifactStatus.Rejected, "Purge leave history");
        var story = w.Add(ArtifactType.UserStory, "US-001", ArtifactStatus.Approved, "Clean up leave");
        w.Link(story, w.Code("FR-002"));
        w.Link(story, otherRejected);
        var ai = new ScriptedAi();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id));

        Assert.Contains("(FR-002, FR-005)", ex.Message);
        Assert.Empty(ai.UserPrompts);
    }

    [Fact]
    public async Task Acceptance_criteria_are_generated_as_before_for_a_story_with_an_eligible_link_or_no_links()
    {
        var w = World.WithRequirements();
        var mixed = w.Add(ArtifactType.UserStory, "US-001", ArtifactStatus.AiGenerated, "Request leave");
        w.Link(mixed, w.Code("FR-002"));
        w.Link(mixed, w.Code("FR-001"));
        var unlinked = w.Add(ArtifactType.UserStory, "US-002", ArtifactStatus.Draft, "See my balance");
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(mixed.Id));
        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(unlinked.Id));

        Assert.Equal(2, ai.UserPrompts.Count);
        Assert.Null(await w.Stories(ai).GenerateAcceptanceCriteriaAsync(w.Code("FR-001").Id)); // not a story: unchanged
    }

    [Fact]
    public async Task The_refusal_reaches_the_api_as_the_established_409_conflict()
    {
        var w = World.WithRequirements();
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => w.Tests(new ScriptedAi()).GenerateAsync(w.Code("FR-002").Id));

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddProblemDetails();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = "/api/artifacts/x/generate-test-cases";
        context.Response.Body = new MemoryStream();

        var handled = await new ApplicationExceptionHandler(NullLogger<ApplicationExceptionHandler>.Instance)
            .TryHandleAsync(context, refusal, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var body = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Equal("conflict", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(refusal.Message, body.RootElement.GetProperty("detail").GetString());
    }

    // ---- agents ---------------------------------------------------------------------------------

    [Fact]
    public void Qa_selection_skips_rejected_and_already_tested_requirements()
    {
        var w = World.WithRequirements();
        var frs = InMemoryAiReapDb.Rows(w.Db.Artifacts);

        var selected = QaAgent.SelectForTestGeneration(frs, new HashSet<Guid> { w.Code("FR-003").Id }).Select(f => f.Code);

        Assert.Equal(new[] { "FR-001", "FR-004" }, selected);
    }

    [Fact]
    public async Task Qa_agent_generates_test_cases_only_for_requirements_that_are_not_rejected()
    {
        var w = World.WithRequirements();
        var tests = new RecordingTestGeneration();

        await new QaAgent(tests, w.Db).RunAsync(new AgentContext(w.Source.ProjectId, w.Source.Id), CancellationToken.None);

        Assert.Equal(new[] { "FR-001", "FR-003", "FR-004" }, tests.Requested.Select(id => w.Artifact(id).Code).Order());
    }

    [Fact]
    public async Task Architecture_agent_stops_when_every_requirement_is_rejected_and_designs_nothing()
    {
        var w = new World();
        w.Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Rejected, RejectedTitle);
        w.Add(ArtifactType.FunctionalRequirement, "FR-002", ArtifactStatus.Rejected, "Purge leave history");
        var design = new RecordingDesign();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            design.Agent(w.Db).RunAsync(new AgentContext(w.Source.ProjectId, w.Source.Id), CancellationToken.None));

        Assert.StartsWith("Every functional requirement for this source has been rejected", ex.Message);
        Assert.Equal(0, design.Calls);
    }

    [Fact]
    public async Task Architecture_agent_keeps_its_no_requirements_message_and_designs_when_one_is_eligible()
    {
        var empty = new World();
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecordingDesign().Agent(empty.Db).RunAsync(new AgentContext(empty.Source.ProjectId, empty.Source.Id), CancellationToken.None));
        Assert.StartsWith("There are no functional requirements to design against.", none.Message);

        var w = World.WithRequirements();
        var design = new RecordingDesign();
        await design.Agent(w.Db).RunAsync(new AgentContext(w.Source.ProjectId, w.Source.Id), CancellationToken.None);
        Assert.Equal(3, design.Calls);
    }

    [Fact]
    public void Acceptance_criteria_eligibility_follows_the_story_and_its_linked_requirements()
    {
        Artifact Fr(ArtifactStatus status) => new() { ArtifactType = ArtifactType.FunctionalRequirement, Status = status };
        var story = new Artifact { ArtifactType = ArtifactType.UserStory, Status = ArtifactStatus.Approved };

        Assert.True(GenerationInputs.CanGenerateAcceptanceCriteria(story, []));
        Assert.True(GenerationInputs.CanGenerateAcceptanceCriteria(story, [Fr(ArtifactStatus.Rejected), Fr(ArtifactStatus.Draft)]));
        Assert.False(GenerationInputs.CanGenerateAcceptanceCriteria(story, [Fr(ArtifactStatus.Rejected), Fr(ArtifactStatus.Rejected)]));
        Assert.False(GenerationInputs.CanGenerateAcceptanceCriteria(
            new Artifact { ArtifactType = ArtifactType.UserStory, Status = ArtifactStatus.Rejected }, [Fr(ArtifactStatus.Approved)]));
    }

    [Fact]
    public async Task Analysis_agent_skips_criteria_for_rejected_stories_and_stories_built_only_on_rejected_requirements()
    {
        var w = World.WithRequirements();
        w.Add(ArtifactType.BusinessRule, "BR-001", ArtifactStatus.Approved);
        var rejectedStory = w.Add(ArtifactType.UserStory, "US-001", ArtifactStatus.Rejected, "Request leave");
        w.Link(rejectedStory, w.Code("FR-001"));
        var onRejectedOnly = w.Add(ArtifactType.UserStory, "US-002", ArtifactStatus.Approved, "Delete leave");
        w.Link(onRejectedOnly, w.Code("FR-002"));
        var mixed = w.Add(ArtifactType.UserStory, "US-003", ArtifactStatus.AiGenerated, "Manage leave");
        w.Link(mixed, w.Code("FR-001"));
        w.Link(mixed, w.Code("FR-002"));
        w.Add(ArtifactType.UserStory, "US-004", ArtifactStatus.Draft, "See my balance"); // no FR links
        var analysis = new RecordingAnalysis();

        // Before A4a the service's refusal would have failed the whole stage; the agent now skips those stories.
        await analysis.Agent(w.Db).RunAsync(new AgentContext(w.Source.ProjectId, w.Source.Id), CancellationToken.None);

        Assert.Equal(new[] { "US-003", "US-004" }, analysis.CriteriaRequested.Select(id => w.Artifact(id).Code).Order());
    }

    // ---- R1: every FR rejected ------------------------------------------------------------------

    private const string AllRejected = "Every functional requirement for this source has been rejected";

    // FR-001 and FR-002, both rejected.
    private static World AllFrsRejected()
    {
        var w = new World();
        w.Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Rejected, RejectedTitle);
        w.Add(ArtifactType.FunctionalRequirement, "FR-002", ArtifactStatus.Rejected, "Purge leave history");
        return w;
    }

    [Theory]
    [MemberData(nameof(PromptStages))]
    public async Task A_source_whose_requirements_are_all_rejected_is_refused_with_409_before_any_ai_call_or_save(string stage)
    {
        var w = AllFrsRejected();
        w.Add(ArtifactType.DataEntity, "DE-001", ArtifactStatus.Approved, "Leave request entity"); // planning has other inputs
        var ai = new ScriptedAi();
        var artifactsBefore = InMemoryAiReapDb.Rows(w.Db.Artifacts).Count;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Run(stage, w, ai));

        Assert.StartsWith(AllRejected, ex.Message);
        Assert.Empty(ai.UserPrompts); // no AI provider call, not even a repair attempt
        Assert.Equal(0, w.Db.SaveCount);
        Assert.Equal(artifactsBefore, InMemoryAiReapDb.Rows(w.Db.Artifacts).Count);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.AIExecutions));
    }

    [Theory]
    [MemberData(nameof(PromptStages))]
    public async Task A_source_with_no_requirements_still_generates_from_its_raw_text_as_before(string stage)
    {
        var w = new World();
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, w, ai));

        var prompt = Assert.Single(ai.UserPrompts);
        Assert.StartsWith(w.Source.RawText, prompt);
        Assert.DoesNotContain("Functional requirements derived so far", prompt);
    }

    [Theory]
    [MemberData(nameof(PromptStages))]
    public async Task One_requirement_left_unrejected_lets_generation_continue_on_it(string stage)
    {
        var w = AllFrsRejected();
        w.Add(ArtifactType.FunctionalRequirement, "FR-003", ArtifactStatus.UnderReview, "View leave balance");
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, w, ai));

        var prompt = Assert.Single(ai.UserPrompts);
        Assert.Contains("FR-003: View leave balance", prompt);
        Assert.DoesNotContain("FR-001", prompt);
        Assert.DoesNotContain("FR-002", prompt);
    }

    [Fact]
    public async Task Stories_for_a_source_with_no_requirements_are_still_saved_as_before()
    {
        var w = new World();
        var ai = new ScriptedAi(
            """{"userStories":[{"title":"Request leave","persona":"Employee","valueStatement":"plan time off","priority":"High","relatedRequirementCodes":[]}]}""");

        var stories = await w.Stories(ai).GenerateStoriesAsync(w.Source.Id);

        Assert.Single(stories);
        Assert.Equal(1, w.Db.SaveCount);
        Assert.Contains(InMemoryAiReapDb.Rows(w.Db.Artifacts), a => a.ArtifactType == ArtifactType.UserStory);
    }

    [Fact]
    public async Task Analysis_agent_refuses_a_source_whose_requirements_are_all_rejected()
    {
        var w = AllFrsRejected(); // no rules or stories yet: without the guard both would be generated
        var analysis = new RecordingAnalysis();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analysis.Agent(w.Db).RunAsync(new AgentContext(w.Source.ProjectId, w.Source.Id), CancellationToken.None));

        Assert.Equal(GenerationInputs.AllRejectedMessage("build business rules, user stories or acceptance criteria on"), ex.Message);
        Assert.Empty(analysis.CriteriaRequested);
    }

    [Fact]
    public async Task Development_planning_agent_refuses_all_rejected_and_plans_otherwise_as_before()
    {
        var rejected = AllFrsRejected();
        var refusedPlanning = new RecordingPlanning();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DevelopmentPlanningAgent(refusedPlanning, rejected.Db)
                .RunAsync(new AgentContext(rejected.Source.ProjectId, rejected.Source.Id), CancellationToken.None));
        Assert.Equal(GenerationInputs.AllRejectedMessage("plan implementation tasks for"), ex.Message);
        Assert.Equal(0, refusedPlanning.Calls);

        foreach (var w in new[] { new World(), World.WithRequirements() }) // no FRs; at least one eligible FR
        {
            var planning = new RecordingPlanning();
            await new DevelopmentPlanningAgent(planning, w.Db).RunAsync(new AgentContext(w.Source.ProjectId, w.Source.Id), CancellationToken.None);
            Assert.Equal(1, planning.Calls);
        }
    }

    [Fact]
    public async Task Rejecting_every_requirement_between_agent_stages_stops_planning_before_any_ai_call()
    {
        var w = new World();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Approved, "Submit leave request");
        var context = new AgentContext(w.Source.ProjectId, w.Source.Id);

        // The Architecture stage runs while the requirement is still eligible...
        var design = new RecordingDesign();
        await design.Agent(w.Db).RunAsync(context, CancellationToken.None);
        Assert.Equal(3, design.Calls);

        // ...then a reviewer rejects it while the run awaits its decision, and the next stage runs.
        fr.Status = ArtifactStatus.Rejected;
        var ai = new ScriptedAi();
        var planning = new ImplementationPlanningService(ai, w.Db, w.User, w.Access, NullLogger<ImplementationPlanningService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DevelopmentPlanningAgent(planning, w.Db).RunAsync(context, CancellationToken.None));

        Assert.StartsWith(AllRejected, ex.Message);
        Assert.Empty(ai.UserPrompts);
        Assert.Equal(0, w.Db.SaveCount);
        Assert.DoesNotContain(InMemoryAiReapDb.Rows(w.Db.Artifacts), a => a.ArtifactType == ArtifactType.ImplementationTask);
        // A direct planning request after the rejection is refused the same way, as a 409.
        await Assert.ThrowsAsync<ConflictException>(() => planning.GenerateAsync(w.Source.Id));
        Assert.Empty(ai.UserPrompts);
    }

    // ---- A4b phase 1: cited codes resolve as they are validated -------------------------------

    [Fact]
    public void Cited_codes_resolve_trimmed_case_insensitively_once_each_to_the_stored_candidates()
    {
        var w = World.WithRequirements();
        var candidates = InMemoryAiReapDb.Rows(w.Db.Artifacts).Where(GenerationInputs.IsEligible).OrderBy(a => a.Code).ToList();

        var resolved = GenerationSupport.ResolveCited([" fr-003 ", "FR-001", "Fr-001", "FR-001", "FR-002", "FR-999", "", "  "], candidates);

        Assert.Equal(new[] { "FR-001", "FR-003" }, resolved.Select(a => a.Code)); // candidate order, canonical codes
        Assert.Same(w.Code("FR-001"), resolved[0]);                             // the stored artifact itself
        Assert.Empty(GenerationSupport.ResolveCited(null, candidates));
        Assert.Empty(GenerationSupport.ResolveCited(["FR-001"], []));
    }

    // One response item per stage citing CODES; the relationship type each stage creates to an FR.
    public static TheoryData<string, string, RelationshipType> LinkingStages => new()
    {
        { "UserStoryGeneration", """{"userStories":[{"title":"Request leave","persona":"Employee","valueStatement":"plan time off","priority":"High","relatedRequirementCodes":CODES}]}""", RelationshipType.DerivedFrom },
        { "BusinessRuleExtraction", """{"businessRules":[{"title":"Notice period","statement":"Leave is requested 2 days ahead.","relatedRequirementCodes":CODES}]}""", RelationshipType.LinkedRule },
        { "DatabaseDesignGeneration", """{"dataEntities":[{"title":"LeaveRequest","fields":[{"name":"Id","dataType":"Guid","required":true}],"keys":["Id"],"relationships":null,"indexes":[],"relatedRequirementCodes":CODES}]}""", RelationshipType.DerivedFrom },
        { "ApiDesignGeneration", """{"apiSpecifications":[{"title":"Submit leave","purpose":"Create a request","method":"POST","route":"/api/leave-requests","request":null,"response":null,"validation":null,"authorization":null,"relatedRequirementCodes":CODES}]}""", RelationshipType.DerivedFrom },
        { "ImplementationPlanning", """{"tasks":[{"title":"Create leave form","description":"React form","taskType":"Frontend","priority":"High","dependencies":[],"suggestedRole":null,"estimate":null,"relatedRequirementCodes":CODES}]}""", RelationshipType.Implements },
    };

    [Theory]
    [MemberData(nameof(LinkingStages))]
    public async Task Whitespace_case_and_repeated_citations_link_once_to_the_canonical_requirement(string stage, string template, RelationshipType type)
    {
        var w = World.WithRequirements();
        // FR-001 only in variant forms (exact matching loses it); FR-003 repeated exactly (per-citation linking duplicates it).
        var ai = new ScriptedAi(template.Replace("CODES", """[" fr-001 ","Fr-001","FR-003","FR-003"]"""));

        var created = Assert.Single((await Run(stage, w, ai))!);

        var links = InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships);
        // The in-memory set does not enforce the SQL unique index (source, type, target): assert it here.
        Assert.Equal(links.Count, links.Select(l => (l.SourceArtifactId, l.RelationshipType, l.TargetArtifactId)).Distinct().Count());
        Assert.All(links, l => Assert.Equal(created.Id, l.SourceArtifactId));
        Assert.All(links, l => Assert.Equal(type, l.RelationshipType));
        Assert.Equal(new[] { w.Code("FR-001").Id, w.Code("FR-003").Id }.Order(), links.Select(l => l.TargetArtifactId).Order());
        Assert.Single(ai.UserPrompts); // accepted first time: these variants always passed validation
        Assert.Equal(1, w.Db.SaveCount);
    }

    [Theory]
    [MemberData(nameof(LinkingStages))]
    public async Task A_null_citation_list_is_saved_without_links_instead_of_failing(string stage, string template, RelationshipType type)
    {
        _ = type;
        var w = new World(); // no FRs: citations are not required (with FRs, see the A4b phase 2 tests)
        var ai = new ScriptedAi(template.Replace("CODES", "null"));

        Assert.Single((await Run(stage, w, ai))!);

        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal(1, w.Db.SaveCount);
    }

    [Theory]
    [InlineData("DE-001")]
    [InlineData("API-001")]
    [InlineData("de-001")]
    public async Task A_task_citing_a_data_entity_or_api_code_as_its_requirement_is_refused_and_nothing_is_saved(string code)
    {
        var w = World.WithRequirements();
        w.Add(ArtifactType.DataEntity, "DE-001", ArtifactStatus.Approved, "Leave request entity");
        w.Add(ArtifactType.ApiSpecification, "API-001", ArtifactStatus.Approved, "Submit leave endpoint");
        var response = $$"""{"tasks":[{"title":"Create leave table","description":"Migration","taskType":"Database","priority":"High","dependencies":[],"suggestedRole":null,"estimate":null,"relatedRequirementCodes":["{{code}}"]}]}""";
        var ai = new ScriptedAi(response, response);
        var artifactsBefore = InMemoryAiReapDb.Rows(w.Db.Artifacts).Count;

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => Run("ImplementationPlanning", w, ai));

        Assert.Contains(ex.Errors, e => e.Contains($"'{code}'") && e.Contains("not one of the artifacts provided"));
        Assert.Equal(2, ai.UserPrompts.Count); // the request and its one repair attempt
        // Data entities and API endpoints are still given to the model as context.
        Assert.Contains("DE-001: Leave request entity", ai.UserPrompts[0]);
        Assert.Contains("API-001: Submit leave endpoint", ai.UserPrompts[0]);
        Assert.Equal(0, w.Db.SaveCount);
        Assert.Equal(artifactsBefore, InMemoryAiReapDb.Rows(w.Db.Artifacts).Count);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
    }

    [Fact]
    public async Task A_task_citing_an_fr_alongside_design_context_still_links_only_that_fr()
    {
        var w = World.WithRequirements();
        w.Add(ArtifactType.DataEntity, "DE-001", ArtifactStatus.Approved, "Leave request entity");
        var ai = new ScriptedAi("""{"tasks":[{"title":"Create leave table","description":"Migration","taskType":"Database","priority":"High","dependencies":["DE-001"],"suggestedRole":null,"estimate":null,"relatedRequirementCodes":["fr-001"]}]}""");

        var task = Assert.Single((await Run("ImplementationPlanning", w, ai))!);

        var link = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal((task.Id, RelationshipType.Implements, w.Code("FR-001").Id), (link.SourceArtifactId, link.RelationshipType, link.TargetArtifactId));
    }

    // ---- A4b phase 2: stories, data entities, API specs and tasks must cite an eligible FR ------

    public static TheoryData<string, string, string> CitationRequiredStages => new()
    {
        { "UserStoryGeneration", LinkingTemplate("UserStoryGeneration"), "UserStoryGeneration-v3" },
        { "DatabaseDesignGeneration", LinkingTemplate("DatabaseDesignGeneration"), "DatabaseDesignGeneration-v3" },
        { "ApiDesignGeneration", LinkingTemplate("ApiDesignGeneration"), "ApiDesignGeneration-v3" },
        { "ImplementationPlanning", LinkingTemplate("ImplementationPlanning"), "ImplementationPlanning-v4" },
    };

    private static string LinkingTemplate(string stage) =>
        (string)LinkingStages.Single(row => (string)row[0] == stage)[1];

    [Fact]
    public void Citations_are_required_only_when_codes_were_provided_and_only_non_blank_ones_count()
    {
        string[] Errors(IEnumerable<string>? known, List<string>? codes)
        {
            var v = new AiResponseValidator(known);
            v.CodesRequired(codes, "item.relatedRequirementCodes");
            return v.Errors.ToArray();
        }

        Assert.Empty(Errors(null, []));            // the caller supplied no known codes
        Assert.Empty(Errors([], null));            // a source without FRs
        Assert.Empty(Errors(["FR-001"], ["FR-001"]));
        Assert.Single(Errors(["FR-001"], null));
        Assert.Single(Errors(["FR-001"], []));
        Assert.Equal("item.relatedRequirementCodes must cite at least one of the artifacts provided.",
            Assert.Single(Errors(["FR-001"], [" ", ""])));
    }

    public static TheoryData<string, string, string> UnsatisfyingCitations
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var stage in new[] { "UserStoryGeneration", "DatabaseDesignGeneration", "ApiDesignGeneration", "ImplementationPlanning" })
            {
                data.Add(stage, "[]", "must cite at least one");
                data.Add(stage, "null", "must cite at least one");
                data.Add(stage, "[\"FR-002\"]", "'FR-002', which is not one of the artifacts provided"); // rejected
                data.Add(stage, "[\"FR-999\"]", "'FR-999', which is not one of the artifacts provided"); // unknown
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnsatisfyingCitations))]
    public async Task Without_a_citation_of_an_eligible_requirement_nothing_is_saved_after_the_repair_attempt(string stage, string codes, string error)
    {
        var w = World.WithRequirements(); // FR-001 Approved, FR-002 Rejected, FR-003 AiGenerated, FR-004 Draft
        var response = LinkingTemplate(stage).Replace("CODES", codes);
        var ai = new ScriptedAi(response, response);
        var artifactsBefore = InMemoryAiReapDb.Rows(w.Db.Artifacts).Count;

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => Run(stage, w, ai));

        Assert.Contains(ex.Errors, e => e.Contains(error));
        Assert.Equal(2, ai.UserPrompts.Count); // the request and its single repair attempt
        Assert.Equal(0, w.Db.SaveCount);
        Assert.Equal(artifactsBefore, InMemoryAiReapDb.Rows(w.Db.Artifacts).Count);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.AIExecutions));
    }

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task A_missing_citation_is_repaired_once_and_the_repaired_answer_is_saved(string stage, string template, string version)
    {
        _ = version;
        var w = World.WithRequirements();
        var ai = new ScriptedAi(template.Replace("CODES", "[]"), template.Replace("CODES", "[\"FR-003\"]"));

        var created = Assert.Single((await Run(stage, w, ai))!);

        Assert.Contains("must cite at least one", ai.UserPrompts[1]); // the repair names the problem
        var link = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal((created.Id, w.Code("FR-003").Id), (link.SourceArtifactId, link.TargetArtifactId));
    }

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task A_valid_citation_is_saved_with_its_link_under_the_bumped_template_version(string stage, string template, string version)
    {
        var w = World.WithRequirements();
        var ai = new ScriptedAi(template.Replace("CODES", "[\"FR-001\"]"));

        var created = Assert.Single((await Run(stage, w, ai))!);

        var link = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal((created.Id, w.Code("FR-001").Id), (link.SourceArtifactId, link.TargetArtifactId));
        Assert.Equal(version, Assert.Single(InMemoryAiReapDb.Rows(w.Db.AIExecutions)).PromptTemplateVersion);
        Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
    }

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task Without_any_requirements_an_empty_citation_list_is_still_saved_as_before(string stage, string template, string version)
    {
        _ = version;
        var w = new World();
        var ai = new ScriptedAi(template.Replace("CODES", "[]"));

        Assert.Single((await Run(stage, w, ai))!);

        Assert.Single(ai.UserPrompts);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal(1, w.Db.SaveCount);
    }

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task All_rejected_requirements_are_still_refused_with_409_before_any_ai_call(string stage, string template, string version)
    {
        _ = (template, version);
        var w = AllFrsRejected();
        var ai = new ScriptedAi();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Run(stage, w, ai));

        Assert.StartsWith(AllRejected, ex.Message);
        Assert.Empty(ai.UserPrompts);
        Assert.Equal(0, w.Db.SaveCount);
    }

    [Fact]
    public async Task Business_rules_and_solution_design_still_need_no_citation()
    {
        var w = World.WithRequirements();
        var ai = new ScriptedAi(
            LinkingTemplate("BusinessRuleExtraction").Replace("CODES", "[]"),
            """{"title":"Leave platform","architectureOverview":"Web app and API","modules":["Leave"]}""");

        Assert.Single((await Run("BusinessRuleExtraction", w, ai))!);
        Assert.Single((await Run("SolutionDesignGeneration", w, ai))!);

        Assert.Equal(2, ai.UserPrompts.Count); // both accepted first time
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
    }

    [Fact]
    public async Task Only_the_four_changed_prompts_carry_new_template_versions()
    {
        var w = World.WithRequirements();
        var story = w.Add(ArtifactType.UserStory, "US-001", ArtifactStatus.Approved, "Request leave");
        w.Link(story, w.Code("FR-001"));
        var ai = new ScriptedAi(
            LinkingTemplate("BusinessRuleExtraction").Replace("CODES", "[\"FR-001\"]"),
            """{"title":"Leave platform","architectureOverview":"Web app and API","modules":["Leave"]}""",
            """{"acceptanceCriteria":[{"title":"Valid request","given":"an employee","when":"they submit","then":"it is saved","kind":"positive"}]}""",
            """{"testCases":[{"title":"Submit","preconditions":null,"steps":["Submit a request"],"expectedResult":"Saved","testKind":"positive"}]}""");

        await Run("BusinessRuleExtraction", w, ai);
        await Run("SolutionDesignGeneration", w, ai);
        await w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id);
        await w.Tests(ai).GenerateAsync(w.Code("FR-001").Id);

        Assert.Equal(
            // A4b Phase 3 bumped acceptance criteria and test cases (EvidenceAwarePromptTests).
            new[] { "AcceptanceCriteriaGeneration-v2", "BusinessRuleExtraction-v1", "SolutionDesignGeneration-v1", "TestCaseGeneration-v2" },
            InMemoryAiReapDb.Rows(w.Db.AIExecutions).Select(e => e.PromptTemplateVersion).Distinct().Order());
    }

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task The_development_stub_cites_a_provided_requirement_so_its_answers_still_pass(string stage, string template, string version)
    {
        _ = (template, version);
        var w = World.WithRequirements();

        var created = Assert.Single((await Run(stage, w, new AiReap.Infrastructure.Ai.StubAiChatClient()))!);

        // The stub cites the first FR code in the prompt: FR-001 heads the eligible list (the raw text has none).
        var link = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal((created.Id, w.Code("FR-001").Id), (link.SourceArtifactId, link.TargetArtifactId));
    }

    // ---- development stub: FR citations come from the eligible-FR list only -----------------------

    // The stages whose stub answer cites an FR (the "FR-001" placeholder in StubAiChatClient).
    public static TheoryData<string> StubCitingStages => new()
    {
        "UserStoryGeneration", "BusinessRuleExtraction", "DatabaseDesignGeneration", "ApiDesignGeneration", "ImplementationPlanning"
    };

    private static AiReap.Infrastructure.Ai.StubAiChatClient Stub() => new();

    [Theory]
    [MemberData(nameof(StubCitingStages))]
    public async Task The_stub_cites_the_listed_requirement_not_an_fr_like_id_in_the_raw_text(string stage)
    {
        var w = new World();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-004", ArtifactStatus.Approved, "Submit leave request");
        w.Source.RawText = "FR-012 Employees submit leave requests. FR-001 Managers approve them.";

        var created = await Run(stage, w, Stub());

        Assert.NotEmpty(created!);
        var links = InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships);
        Assert.Equal(created!.Count, links.Count);
        Assert.All(links, l => Assert.Equal(fr.Id, l.TargetArtifactId));
        Assert.Equal(1, w.Db.SaveCount);
    }

    [Theory]
    [MemberData(nameof(StubCitingStages))]
    public async Task A_raw_text_id_that_collides_with_a_stored_code_does_not_steer_the_stub(string stage)
    {
        var w = World.WithRequirements(); // FR-001 Approved, FR-002 Rejected, FR-003 AiGenerated, FR-004 Draft
        // The document's own IDs reuse stored codes: FR-002 (rejected) and FR-003 come first in the prompt.
        w.Source.RawText = "FR-002 Managers approve leave. FR-003 Employees cancel leave.";

        var created = await Run(stage, w, Stub());

        Assert.NotEmpty(created!);
        // The first line of the eligible list (FR-001), never the raw text's FR-002 or FR-003.
        Assert.All(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships), l => Assert.Equal(w.Code("FR-001").Id, l.TargetArtifactId));
    }

    [Theory]
    [MemberData(nameof(StubCitingStages))]
    public async Task A_source_without_requirements_keeps_the_no_fr_behaviour_under_the_stub_despite_fr_ids_in_its_text(string stage)
    {
        var w = new World();
        w.Source.RawText = "FR-012 Employees submit leave requests. FR-013 Managers approve them.";

        var created = await Run(stage, w, Stub());

        Assert.NotEmpty(created!);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal(1, w.Db.SaveCount);
    }

    [Theory]
    [InlineData("userStories", "\"userStories\"")]
    [InlineData("businessRules", "\"businessRules\"")]
    [InlineData("dataEntities", "\"dataEntities\"")]
    [InlineData("apiSpecifications", "\"apiSpecifications\"")]
    [InlineData("taskType", "\"tasks\"")]
    public async Task The_stub_routes_each_stage_and_reads_codes_only_from_the_list_under_the_heading(string systemKeyword, string rootKey)
    {
        var listed = $"FR-012 The document's own ID.\n\n{GenerationSupport.FunctionalRequirementsHeading}\n- FR-004: Submit leave request\n- FR-007: Cancel leave\n";

        var withList = await Stub().CompleteAsync(systemKeyword, listed);
        var noHeading = await Stub().CompleteAsync(systemKeyword, "FR-012 Employees submit leave requests.\n\nClarifications gathered so far:\n- Q: FR-013?\n");
        var headingWithoutList = await Stub().CompleteAsync(systemKeyword, $"FR-012 Text.\n\n{GenerationSupport.FunctionalRequirementsHeading}\nNo items.\n");

        Assert.Contains(rootKey, withList);
        Assert.Contains("\"relatedRequirementCodes\": [\"FR-004\"]", withList);
        Assert.DoesNotContain("FR-012", withList);
        Assert.DoesNotContain("FR-007", withList);
        foreach (var response in new[] { noHeading, headingWithoutList })
        {
            Assert.Contains(rootKey, response);
            Assert.Contains("\"relatedRequirementCodes\": []", response);
            Assert.DoesNotContain("FR-0", response); // no code invented from the raw text
        }
    }

    [Fact]
    public async Task The_stub_still_cites_a_business_objective_from_anywhere_in_the_prompt()
    {
        var withObjective = await Stub().CompleteAsync("functionalRequirements", "Raw text.\n\nBusiness objectives of this project:\n- BO-002: Faster approvals\n");
        var without = await Stub().CompleteAsync("functionalRequirements", "Raw text.");

        Assert.Contains("\"businessObjectiveCodes\": [\"BO-002\"]", withObjective);
        Assert.Contains("\"businessObjectiveCodes\": []", without);
    }

    // ---- A4b phase 4: listed FRs show their source IDs; raw-text IDs are not citation codes ------

    private const string Heading = GenerationSupport.FunctionalRequirementsHeading;

    // The collision: stored FR-004 came from the document's "FR-001", and stored FR-001 from its
    // "FR-012". FR-002 (rejected) came from "FR-099"; FR-003 records no source IDs.
    private static World CollidingSourceIds()
    {
        var w = new World();
        w.Source.RawText = "FR-001 Managers approve leave requests.\nFR-012 Employees submit leave requests.";
        w.AddFr("FR-001", ArtifactStatus.Approved, "Submit leave request", "FR-012");
        w.AddFr("FR-002", ArtifactStatus.Rejected, RejectedTitle, "FR-099");
        w.AddFr("FR-003", ArtifactStatus.AiGenerated, "View leave balance");
        w.AddFr("FR-004", ArtifactStatus.Draft, "Approve leave request", " FR-001 ", "FR-020", "fr-020", "");
        return w;
    }

    private const string CollidingBlock =
        $"\n\n{Heading}\n" +
        "- FR-001: Submit leave request (source IDs: FR-012)\n" +
        "- FR-003: View leave balance\n" +
        "- FR-004: Approve leave request (source IDs: FR-001, FR-020)\n";

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task Each_listed_requirement_shows_its_source_ids_beside_its_stored_code(string stage, string template, string version)
    {
        _ = (template, version);
        var w = CollidingSourceIds();
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, w, ai));

        var prompt = Assert.Single(ai.UserPrompts);
        Assert.StartsWith(w.Source.RawText, prompt);
        Assert.EndsWith(CollidingBlock, prompt); // code order kept; blank and repeated IDs dropped
        Assert.DoesNotContain("FR-002", prompt);
        Assert.DoesNotContain("FR-099", prompt); // a rejected FR's source IDs stay out with it
        Assert.DoesNotContain(RejectedTitle, prompt);
    }

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task Requirements_without_source_ids_keep_the_previous_prompt_byte_for_byte(string stage, string template, string version)
    {
        _ = (template, version);
        var w = World.WithRequirements(); // payload "{}" - no SourceIds at all
        w.AddFr("FR-005", ArtifactStatus.Approved, "Empty source IDs");
        w.AddFr("FR-006", ArtifactStatus.Approved, "Blank source IDs", " ", "");
        w.Add(ArtifactType.FunctionalRequirement, "FR-007", ArtifactStatus.Approved, "Unreadable payload").DataJson = "not json";
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, w, ai));

        Assert.Equal(
            w.Source.RawText + $"\n\n{Heading}\n" +
            "- FR-001: Submit leave request\n- FR-003: View leave balance\n- FR-004: Cancel leave request\n" +
            "- FR-005: Empty source IDs\n- FR-006: Blank source IDs\n- FR-007: Unreadable payload\n",
            Assert.Single(ai.UserPrompts));
    }

    [Fact]
    public void The_shared_block_equals_the_plain_block_when_no_requirement_records_source_ids()
    {
        var w = World.WithRequirements();
        var eligible = InMemoryAiReapDb.Rows(w.Db.Artifacts).Where(GenerationInputs.IsEligible).OrderBy(a => a.Code, StringComparer.Ordinal).ToList();

        Assert.Equal(GenerationSupport.BuildContextBlock(Heading, eligible), GenerationSupport.BuildFunctionalRequirementBlock(eligible));
        Assert.Equal(string.Empty, GenerationSupport.BuildFunctionalRequirementBlock([]));
    }

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task A_document_id_mapped_to_a_different_stored_code_is_linked_by_that_stored_code(string stage, string template, string version)
    {
        var w = CollidingSourceIds();
        // The model means the document's "FR-001" (manager approval) and cites the stored code shown beside it.
        var ai = new ScriptedAi(template.Replace("CODES", "[\"FR-004\"]"));

        var created = Assert.Single((await Run(stage, w, ai))!);

        var link = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal((created.Id, w.Code("FR-004").Id), (link.SourceArtifactId, link.TargetArtifactId));
        Assert.Equal(version, Assert.Single(InMemoryAiReapDb.Rows(w.Db.AIExecutions)).PromptTemplateVersion);
    }

    public static TheoryData<string, string> UnstoredOrRejectedCitations
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var stage in new[] { "UserStoryGeneration", "DatabaseDesignGeneration", "ApiDesignGeneration", "ImplementationPlanning" })
            {
                data.Add(stage, "FR-012"); // a document ID shown only as a source ID, not a stored code
                data.Add(stage, "FR-020"); // likewise
                data.Add(stage, "FR-099"); // the source ID of a rejected FR
                data.Add(stage, "FR-002"); // a rejected stored code
                data.Add(stage, "FR-999"); // unknown
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnstoredOrRejectedCitations))]
    public async Task Citing_a_source_id_or_a_rejected_or_unknown_code_is_still_refused_and_nothing_is_saved(string stage, string code)
    {
        var w = CollidingSourceIds();
        var response = LinkingTemplate(stage).Replace("CODES", $"[\"{code}\"]");
        var ai = new ScriptedAi(response, response);
        var artifactsBefore = InMemoryAiReapDb.Rows(w.Db.Artifacts).Count;

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => Run(stage, w, ai));

        Assert.Contains(ex.Errors, e => e.Contains($"'{code}', which is not one of the artifacts provided"));
        Assert.Equal(0, w.Db.SaveCount);
        Assert.Equal(artifactsBefore, InMemoryAiReapDb.Rows(w.Db.Artifacts).Count);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.AIExecutions));
    }

    // The A4b phase 4 rule, line by line as it appears in each of the four system prompts.
    private static readonly string[] CitationRuleLines =
    [
        "Cite a requirement only by its stored code - the code on the left of a line in the",
        "\"FR-012\") are document IDs, not citation codes: one can look like, or even equal, a listed",
        "code yet name a different requirement. Where a listed line shows \"(source IDs: ...)\", those",
        "are the document IDs that requirement came from - cite it by the stored code on the left.",
        "Never invent a code or cite an unrelated one.",
    ];

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task Each_fr_citing_system_prompt_says_raw_text_ids_are_not_citation_codes(string stage, string template, string version)
    {
        _ = (template, version);
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, CollidingSourceIds(), ai));

        var system = Assert.Single(ai.SystemPrompts);
        Assert.All(CitationRuleLines, line => Assert.Contains(line, system));
    }

    [Theory]
    [InlineData("BusinessRuleExtraction")]
    [InlineData("SolutionDesignGeneration")]
    public async Task Business_rules_and_solution_design_keep_their_prompts_and_plain_fr_lines(string stage)
    {
        var ai = new ScriptedAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, CollidingSourceIds(), ai));

        Assert.DoesNotContain("source IDs", Assert.Single(ai.UserPrompts));
        Assert.EndsWith("- FR-004: Approve leave request\n", ai.UserPrompts[0]);
        Assert.All(CitationRuleLines, line => Assert.DoesNotContain(line, ai.SystemPrompts[0]));
    }

    [Theory]
    [MemberData(nameof(StubCitingStages))]
    public async Task The_stub_still_cites_the_first_listed_stored_code_when_lines_carry_source_ids(string stage)
    {
        var w = new World();
        w.Source.RawText = "FR-001 Managers approve leave requests.";
        w.AddFr("FR-001", ArtifactStatus.Rejected, RejectedTitle, "FR-050");
        w.AddFr("FR-003", ArtifactStatus.Approved, "Approve leave request", "FR-001");

        var created = Assert.Single((await Run(stage, w, Stub()))!);

        var link = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal((created.Id, w.Code("FR-003").Id), (link.SourceArtifactId, link.TargetArtifactId));
    }

    [Theory]
    [MemberData(nameof(StubCitingStages))]
    public async Task The_stub_cites_the_first_listed_stored_code_when_a_raw_source_id_equals_another_eligible_code(string stage)
    {
        // The raw text's "FR-007" is the document ID of stored FR-003, and also equals stored FR-007's
        // code - a different, eligible requirement. Validation accepts either code; only FR-003 is right.
        var w = new World();
        w.Source.RawText = "FR-007 Employees submit leave requests.";
        w.AddFr("FR-003", ArtifactStatus.Approved, "Submit leave request", "FR-007");
        w.AddFr("FR-007", ArtifactStatus.Approved, "Approve leave request", "FR-003");

        var created = Assert.Single((await Run(stage, w, Stub()))!);

        var link = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactRelationships));
        Assert.Equal((created.Id, w.Code("FR-003").Id), (link.SourceArtifactId, link.TargetArtifactId));
    }

    // ---- A4b phase 4: source IDs count toward the provider budget; nothing is dropped to fit ----

    [Theory]
    [MemberData(nameof(CitationRequiredStages))]
    public async Task A_prompt_its_source_ids_push_over_the_groq_budget_is_refused_before_sending_and_saves_nothing(
        string stage, string template, string version)
    {
        _ = version;
        static World Build(bool withSourceIds, string rawText)
        {
            var w = new World();
            w.Source.RawText = rawText;
            for (var i = 1; i <= 4; i++)
            {
                var ids = withSourceIds ? Enumerable.Range(0, 10).Select(n => $"FR-{100 + i * 10 + n}").ToArray() : [];
                w.AddFr($"FR-00{i}", ArtifactStatus.Approved, $"Requirement {i}", ids);
            }
            return w;
        }

        async Task<(string System, string User)> Built(World w)
        {
            var capture = new ScriptedAi();
            await Assert.ThrowsAsync<PromptCaptured>(() => Run(stage, w, capture));
            return (capture.SystemPrompts[0], capture.UserPrompts[0]);
        }

        static int Tokens((string System, string User) p) => PromptBudgetReducer.EstimateRequestTokens(p.System, p.User);

        // Measure the annotation, then pad the raw text so the plain prompt fits and the annotated one does not.
        var gap = Tokens(await Built(Build(true, "x"))) - Tokens(await Built(Build(false, "x")));
        Assert.True(gap >= 100, $"the source IDs add {gap} estimated tokens");
        var padTokens = GroqInputBudget - gap / 2 - Tokens(await Built(Build(false, "")));
        var raw = DistinctText(padTokens);
        var plain = await Built(Build(false, raw));
        var annotated = await Built(Build(true, raw));
        Assert.InRange(Tokens(plain), 1, GroqInputBudget);
        Assert.True(Tokens(annotated) > GroqInputBudget);

        // Without source IDs: sent whole, exactly as built.
        var fitsInner = new ScriptedAi(template.Replace("CODES", "[\"FR-001\"]"));
        Assert.Single((await Run(stage, Build(false, raw), Groq(fitsInner)))!);
        Assert.Equal(plain.User, Assert.Single(fitsInner.UserPrompts));

        // With them: refused before sending - the source text and FR lines are never shortened to fit.
        var w = Build(true, raw);
        var inner = new ScriptedAi(template.Replace("CODES", "[\"FR-001\"]"));
        var artifactsBefore = InMemoryAiReapDb.Rows(w.Db.Artifacts).Count;

        var ex = await Assert.ThrowsAsync<AiStageOverBudgetException>(() => Run(stage, w, Groq(inner)));

        Assert.Equal(stage, ex.Stage);
        Assert.False(ex.RepairAttempt);
        Assert.Equal(GroqInputBudget, ex.InputBudget);
        Assert.Empty(inner.UserPrompts); // nothing sent
        Assert.Equal(0, w.Db.SaveCount);
        Assert.Equal(artifactsBefore, InMemoryAiReapDb.Rows(w.Db.Artifacts).Count);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.AIExecutions));
    }

    // appsettings.json Ai:Groq: MaxRequestTokens 8000, ReservedOutputTokens 4000.
    private const int GroqInputBudget = 8000 - 4000;

    private static AiReap.Infrastructure.Ai.RequestBudgetAiChatClient Groq(IAiChatClient inner) =>
        new(inner, "Groq", "Ai:Groq:MaxRequestTokens", 8000, 4000, NullLogger.Instance);

    // Distinct sentences estimated at `tokens` tokens - no repeated line, so lossless clean-up cannot shrink it.
    private static string DistinctText(int tokens)
    {
        var chars = (int)(tokens * PromptBudgetReducer.CharsPerToken);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; sb.Length < chars; i++)
        {
            sb.Append($"Statement {i} adds one more distinct detail. ");
        }

        return sb.ToString(0, chars);
    }

    // ---- fixture --------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<ArtifactResponse>?> Run(string stage, World w, IAiChatClient ai) => stage switch
    {
        "UserStoryGeneration" => await w.Stories(ai).GenerateStoriesAsync(w.Source.Id),
        "BusinessRuleExtraction" => await new BusinessRuleService(ai, w.Db, w.User, w.Access, NullLogger<BusinessRuleService>.Instance).GenerateAsync(w.Source.Id),
        "SolutionDesignGeneration" => new[] { await new SolutionDesignService(ai, w.Db, w.User, w.Access, NullLogger<SolutionDesignService>.Instance).GenerateAsync(w.Source.Id) },
        "DatabaseDesignGeneration" => await new DatabaseDesignService(ai, w.Db, w.User, w.Access, NullLogger<DatabaseDesignService>.Instance).GenerateAsync(w.Source.Id),
        "ApiDesignGeneration" => await new ApiDesignService(ai, w.Db, w.User, w.Access, NullLogger<ApiDesignService>.Instance).GenerateAsync(w.Source.Id),
        "ImplementationPlanning" => await new ImplementationPlanningService(ai, w.Db, w.User, w.Access, NullLogger<ImplementationPlanningService>.Instance).GenerateAsync(w.Source.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

    private sealed class World
    {
        public InMemoryAiReapDb Db { get; } = new();
        public RequirementSource Source { get; }
        public ICurrentUser User { get; } = new TestCurrentUser("ba-user", "BusinessAnalyst");
        public IProjectAccessService Access { get; } = new AllowAllAccess();

        public World()
        {
            Source = new RequirementSource { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), RawText = "Employees manage their annual leave." };
            Db.RequirementSources.Add(Source);
        }

        // FR-001 Approved, FR-002 Rejected, FR-003 AiGenerated (awaiting review), FR-004 Draft.
        public static World WithRequirements()
        {
            var w = new World();
            w.Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Approved, "Submit leave request");
            w.Add(ArtifactType.FunctionalRequirement, "FR-002", ArtifactStatus.Rejected, RejectedTitle);
            w.Add(ArtifactType.FunctionalRequirement, "FR-003", ArtifactStatus.AiGenerated, "View leave balance");
            w.Add(ArtifactType.FunctionalRequirement, "FR-004", ArtifactStatus.Draft, "Cancel leave request");
            return w;
        }

        public Artifact Add(ArtifactType type, string code, ArtifactStatus status, string title = "Untitled")
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = Source.ProjectId, RequirementSourceId = Source.Id,
                ArtifactType = type, Code = code, Title = title, Status = status
            };
            Db.Artifacts.Add(artifact);
            return artifact;
        }

        // An FR whose payload records the requirement IDs written in the source it came from.
        public Artifact AddFr(string code, ArtifactStatus status, string title, params string[] sourceIds)
        {
            var fr = Add(ArtifactType.FunctionalRequirement, code, status, title);
            fr.DataJson = JsonSerializer.Serialize(new AiReap.Application.Artifacts.Payloads.FunctionalRequirementPayload
            {
                Actor = "Employee", SourceIds = sourceIds.ToList()
            });
            return fr;
        }

        public void Link(Artifact story, Artifact fr) => Db.ArtifactRelationships.Add(new ArtifactRelationship
        {
            Id = Guid.NewGuid(), SourceArtifactId = story.Id, TargetArtifactId = fr.Id, RelationshipType = RelationshipType.DerivedFrom,
            SourceArtifact = story, TargetArtifact = fr
        });

        public Artifact Code(string code) => InMemoryAiReapDb.Rows(Db.Artifacts).Single(a => a.Code == code);

        public Artifact Artifact(Guid id) => InMemoryAiReapDb.Rows(Db.Artifacts).Single(a => a.Id == id);

        public UserStoryService Stories(IAiChatClient ai) => new(ai, Db, User, Access, NullLogger<UserStoryService>.Instance);

        public TestGenerationService Tests(IAiChatClient ai) => new(ai, Db, User, Access, NullLogger<TestGenerationService>.Instance);
    }

    // Records every user prompt; answers with the scripted responses in order, then throws PromptCaptured
    // so a test can inspect the prompt a stage built without that stage saving anything.
    private sealed class ScriptedAi(params string[] responses) : IAiChatClient
    {
        public List<string> UserPrompts { get; } = new();
        public List<string> SystemPrompts { get; } = new();
        public string ModelName => "scripted-test-model";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            UserPrompts.Add(userPrompt);
            return UserPrompts.Count <= responses.Length ? Task.FromResult(responses[UserPrompts.Count - 1]) : throw new PromptCaptured();
        }
    }

    private sealed class PromptCaptured : Exception;

    private sealed class AllowAllAccess : IProjectAccessService
    {
        public Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingTestGeneration : ITestGenerationService
    {
        public List<Guid> Requested { get; } = new();

        public Task<IReadOnlyList<ArtifactResponse>?> GenerateAsync(Guid functionalRequirementArtifactId, CancellationToken cancellationToken = default)
        {
            Requested.Add(functionalRequirementArtifactId);
            return Task.FromResult<IReadOnlyList<ArtifactResponse>?>(Array.Empty<ArtifactResponse>());
        }
    }

    // The Analysis agent's collaborators: requirements, rules and stories already exist (seeded), so only
    // acceptance-criteria generation, quality and conflict checks are reached.
    private sealed class RecordingAnalysis : IRequirementGenerationService, IBusinessRuleService, IUserStoryService,
        IRequirementQualityService, IConflictDetectionService
    {
        public List<Guid> CriteriaRequested { get; } = new();

        public AnalysisAgent Agent(InMemoryAiReapDb db) => new(this, this, this, this, this, db);

        public Task<IReadOnlyList<ArtifactResponse>?> GenerateAcceptanceCriteriaAsync(Guid userStoryArtifactId, CancellationToken cancellationToken = default)
        {
            CriteriaRequested.Add(userStoryArtifactId);
            return Task.FromResult<IReadOnlyList<ArtifactResponse>?>(Array.Empty<ArtifactResponse>());
        }

        public Task<IReadOnlyList<QualityFinding>> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QualityFinding>>([]);

        public Task<IReadOnlyList<ConflictFinding>> DetectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConflictFinding>>([]);

        Task<GenerateRequirementsResult> IRequirementGenerationService.GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Requirements already exist; not expected to be generated.");

        Task<IReadOnlyList<ArtifactResponse>> IBusinessRuleService.GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Business rules already exist; not expected to be generated.");

        public Task<IReadOnlyList<ArtifactResponse>> GenerateStoriesAsync(Guid requirementSourceId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Stories already exist; not expected to be generated.");
    }

    private sealed class RecordingPlanning : IImplementationPlanningService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ArtifactResponse>> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ArtifactResponse>>([]);
        }
    }

    private sealed class RecordingDesign : ISolutionDesignService, IDatabaseDesignService, IApiDesignService
    {
        public int Calls { get; private set; }

        public ArchitectureAgent Agent(InMemoryAiReapDb db) => new(this, this, this, db);

        Task<ArtifactResponse> ISolutionDesignService.GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ArtifactResponseMapper.ToResponse(new Artifact { Id = Guid.NewGuid(), Code = "DA-001", ArtifactType = ArtifactType.DesignArtifact }));
        }

        Task<IReadOnlyList<ArtifactResponse>> IDatabaseDesignService.GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ArtifactResponse>>([]);
        }

        Task<IReadOnlyList<ArtifactResponse>> IApiDesignService.GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ArtifactResponse>>([]);
        }
    }
}
