using System.Text;
using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// A4b Phase 3 — acceptance-criteria and test-case prompts carry the FR context they build on: a story's
// eligible linked FRs (never a rejected one) after its unchanged v1 prompt, and a test-case FR's inputs,
// origin and evidence results after its unchanged v1 prompt. Evidence is labelled from the persisted
// fields only - a record made before evidence checks reads "not checked", never "none" - and no quote
// text reaches either prompt. Over the Groq budget, both stages are still refused before anything is
// sent or saved. Unit-level: the real services over InMemoryAiReapDb - no SQL Server, no AI provider.
public class EvidenceAwarePromptTests
{
    private const string QuoteSentinel = "QUOTESENTINEL employees may carry over five days";
    private const string RejectedTitle = "REJECTEDSENTINEL purge all leave";
    private const string NotChecked = "not checked (created before evidence checks)";

    private const string AcResponse =
        """{"acceptanceCriteria":[{"title":"Valid request","given":"an employee","when":"they submit","then":"it is saved","kind":"positive"}]}""";

    private const string TcResponse =
        """{"testCases":[{"title":"Submit","preconditions":null,"steps":["Submit a request"],"expectedResult":"Saved","testKind":"positive"}]}""";

    // ---- acceptance criteria ----------------------------------------------------------------------

    [Fact]
    public async Task Eligible_linked_requirements_follow_the_unchanged_story_prompt_in_ordinal_code_order()
    {
        var w = new World();
        var story = w.Story("US-001", ArtifactStatus.Approved);
        // Linked out of order; "FR-010" sorts after "FR-002" ordinally.
        w.Link(story, w.Fr("FR-010", ArtifactStatus.Draft, "Export leave", Checked("verified", ["10 days"])));
        w.Link(story, w.Fr("FR-002", ArtifactStatus.AiGenerated, "View balance", Checked("none")));
        w.Link(story, w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("unverified")));
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id));

        var prompt = Assert.Single(ai.Calls).User;
        Assert.StartsWith(World.StoryPrompt + "\n\n" + FunctionalRequirementContext.LinkedHeading + "\n", prompt);
        var at = new[] { "- FR-001: Submit leave\n", "- FR-002: View balance\n", "- FR-010: Export leave\n" }
            .Select(line => prompt.IndexOf(line, StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i > 0));
        Assert.Equal(at.Order(), at);

        foreach (var field in new[] { "Actor:", "Preconditions:", "Inputs:", "Processing:", "Expected result:", "Requirement origin:", "Evidence status:", "Numbers not found in verified quotes:" })
        {
            Assert.Equal(3, Occurrences(prompt, $"  {field} "));
        }

        Assert.Contains("  Numbers not found in verified quotes: 10 days\n", prompt);
        Assert.Contains("  Evidence status: no quotes given\n", prompt);
        Assert.Contains("  Evidence status: unverified (", prompt);
    }

    [Fact]
    public async Task Rejected_linked_requirements_are_left_out_of_a_mixed_story_prompt()
    {
        var w = new World();
        var story = w.Story("US-001", ArtifactStatus.AiGenerated);
        w.Link(story, w.Fr("FR-002", ArtifactStatus.Rejected, RejectedTitle, Checked("verified")));
        w.Link(story, w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("verified")));
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id));

        var prompt = Assert.Single(ai.Calls).User;
        Assert.Contains("- FR-001: Submit leave\n", prompt);
        Assert.DoesNotContain("FR-002", prompt);
        Assert.DoesNotContain("REJECTEDSENTINEL", prompt);
        Assert.Equal(1, Occurrences(prompt, "  Actor: "));
    }

    [Fact]
    public async Task A_story_with_only_rejected_links_is_still_refused_before_any_ai_call()
    {
        var w = new World();
        var story = w.Story("US-001", ArtifactStatus.Approved);
        w.Link(story, w.Fr("FR-002", ArtifactStatus.Rejected, RejectedTitle, Checked("verified")));
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<ConflictException>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id));

        Assert.Empty(ai.Calls);
        Assert.Equal(0, w.Db.SaveCount);
    }

    [Fact]
    public async Task An_unlinked_story_and_a_story_linked_only_to_a_non_requirement_keep_the_v1_prompt_byte_for_byte()
    {
        var w = new World();
        var unlinked = w.Story("US-001", ArtifactStatus.Draft);
        var linkedToRule = w.Story("US-002", ArtifactStatus.Draft);
        var rule = w.Add(ArtifactType.BusinessRule, "BR-001", ArtifactStatus.Approved, "Rule", "{}");
        w.Link(linkedToRule, rule);
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(unlinked.Id));
        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(linkedToRule.Id));

        Assert.Equal(2, ai.Calls.Count);
        Assert.All(ai.Calls, call => Assert.Equal(World.StoryPrompt, call.User));
    }

    // ---- test cases -------------------------------------------------------------------------------

    [Fact]
    public async Task Test_case_prompt_keeps_the_v1_prompt_as_an_exact_prefix_and_appends_the_evidence_fields()
    {
        var w = new World();
        var fr = w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("verified", ["30 days", "5 MB"], origin: "derived", inputs: "Start and end dates"));
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Tests(ai).GenerateAsync(fr.Id));

        var v1 = "FR-001: Submit leave\nActor: Employee\nPreconditions: Signed in\nProcessing: Records the request\nExpected result: Request is pending";
        Assert.Equal(
            v1 + "\nInputs: Start and end dates\nRequirement origin: derived\n" +
            "Evidence status: verified (its quotes were found in the cited text; this does not confirm the requirement itself)\n" +
            "Numbers not found in verified quotes: 30 days, 5 MB",
            Assert.Single(ai.Calls).User);
    }

    // ---- honest labels ----------------------------------------------------------------------------

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"actor":"Employee","processing":"Records the request"}""")] // a human-edited legacy record (camelCase)
    [InlineData("""{"Actor":"Employee","EvidenceStatus":"verified","UnsupportedNumbers":[]}""")] // no EvidenceCheck: not a check
    public async Task A_requirement_made_before_evidence_checks_is_labelled_not_checked_in_both_prompts(string legacyJson)
    {
        var w = new World();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Approved, "Submit leave", legacyJson);
        var story = w.Story("US-001", ArtifactStatus.Approved);
        w.Link(story, fr);
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Tests(ai).GenerateAsync(fr.Id));
        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id));

        Assert.All(ai.Calls.Select(c => c.User), prompt =>
        {
            Assert.Contains("Inputs: not specified\n", prompt);
            Assert.Contains("Requirement origin: not recorded\n", prompt);
            Assert.Contains($"Evidence status: {NotChecked}\n", prompt);
            Assert.Contains($"Numbers not found in verified quotes: {NotChecked}", prompt);
            Assert.DoesNotContain("Evidence status: verified", prompt);
            Assert.DoesNotContain("Evidence status: no quotes given", prompt);
            Assert.DoesNotContain("verified quotes: none", prompt);
        });
    }

    [Fact]
    public async Task A_checked_requirement_without_quotes_or_unsupported_numbers_is_labelled_as_such()
    {
        var w = new World();
        var fr = w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("none", origin: "assumed"));
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Tests(ai).GenerateAsync(fr.Id));

        var prompt = Assert.Single(ai.Calls).User;
        Assert.EndsWith(
            "\nRequirement origin: assumed\nEvidence status: no quotes given\nNumbers not found in verified quotes: none", prompt);
        Assert.DoesNotContain(NotChecked, prompt);
    }

    [Fact]
    public async Task No_quote_text_or_excerpt_label_reaches_either_prompt()
    {
        var w = new World();
        var fr = w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("verified"));
        var story = w.Story("US-001", ArtifactStatus.Approved);
        w.Link(story, fr);
        var ai = new RecordingAi();

        await Assert.ThrowsAsync<PromptCaptured>(() => w.Tests(ai).GenerateAsync(fr.Id));
        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(ai).GenerateAcceptanceCriteriaAsync(story.Id));

        Assert.Equal(2, ai.Calls.Count);
        Assert.All(ai.Calls.Select(c => c.User), prompt =>
        {
            Assert.Contains("Evidence status: verified (", prompt);
            Assert.DoesNotContain("QUOTESENTINEL", prompt);
            Assert.DoesNotContain("carry over five days", prompt);
            Assert.DoesNotContain("notes.pdf", prompt);
            Assert.DoesNotContain("DERIVATIONSENTINEL", prompt);
        });
    }

    // ---- versions and routing ---------------------------------------------------------------------

    [Fact]
    public async Task Both_stages_record_their_v2_template_version_linked_or_not()
    {
        var w = new World();
        var fr = w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("verified"));
        var linked = w.Story("US-001", ArtifactStatus.Approved);
        w.Link(linked, fr);
        var unlinked = w.Story("US-002", ArtifactStatus.Approved);
        var ai = new RecordingAi(AcResponse, AcResponse, TcResponse);

        await w.Stories(ai).GenerateAcceptanceCriteriaAsync(linked.Id);
        await w.Stories(ai).GenerateAcceptanceCriteriaAsync(unlinked.Id);
        await w.Tests(ai).GenerateAsync(fr.Id);

        var versions = InMemoryAiReapDb.Rows(w.Db.AIExecutions).Select(e => (e.OperationType, e.PromptTemplateVersion)).ToList();
        Assert.Equal(3, versions.Count);
        Assert.Equal(2, versions.Count(v => v == ("AcceptanceCriteriaGeneration", "AcceptanceCriteriaGeneration-v2")));
        Assert.Single(versions, v => v == ("TestCaseGeneration", "TestCaseGeneration-v2"));
    }

    [Fact]
    public async Task The_development_stub_still_routes_both_new_system_prompts_to_their_own_answers()
    {
        var w = new World();
        var fr = w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("verified", ["30 days"]));
        var story = w.Story("US-001", ArtifactStatus.Approved);
        w.Link(story, fr);
        var stub = new StubAiChatClient();

        var criteria = await w.Stories(stub).GenerateAcceptanceCriteriaAsync(story.Id);
        var tests = await w.Tests(stub).GenerateAsync(fr.Id);

        Assert.NotEmpty(criteria!);
        Assert.All(criteria!, a => Assert.Equal(ArtifactType.AcceptanceCriterion, a.ArtifactType));
        Assert.NotEmpty(tests!);
        Assert.All(tests!, a => Assert.Equal(ArtifactType.TestCase, a.ArtifactType));
    }

    // ---- prompt-size safety -----------------------------------------------------------------------

    // appsettings.json Ai:Groq: MaxRequestTokens 8000, ReservedOutputTokens 4000 -> a 4,000-token input budget.
    private const int MaxRequestTokens = 8000;
    private const int ReservedOutputTokens = 4000;

    [Fact]
    public async Task Acceptance_criteria_over_the_groq_budget_are_refused_before_sending_and_save_nothing()
    {
        var w = new World();
        var story = w.Story("US-001", ArtifactStatus.Approved);
        // Three long, distinct linked FRs (~1,500 estimated tokens each): no lossless clean-up can fit them.
        for (var i = 1; i <= 3; i++)
        {
            w.Link(story, w.Fr($"FR-00{i}", ArtifactStatus.Approved, $"Requirement {i}", Checked("verified", processing: LongText(i, 1_500))));
        }

        var inner = new RecordingAi(AcResponse);
        var budgeted = Groq(inner);
        var artifactsBefore = InMemoryAiReapDb.Rows(w.Db.Artifacts).Count;

        var ex = await Assert.ThrowsAsync<AiStageOverBudgetException>(() => w.Stories(budgeted).GenerateAcceptanceCriteriaAsync(story.Id));

        Assert.Equal("AcceptanceCriteriaGeneration", ex.Stage);
        Assert.False(ex.RepairAttempt);
        Assert.Equal(MaxRequestTokens - ReservedOutputTokens, ex.InputBudget);
        Assert.True(ex.OverBudgetTokens > 0);
        Assert.Empty(inner.Calls); // nothing sent
        Assert.Equal(0, w.Db.SaveCount); // nothing saved
        Assert.Equal(artifactsBefore, InMemoryAiReapDb.Rows(w.Db.Artifacts).Count);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.AIExecutions));
    }

    [Fact]
    public async Task Test_cases_over_the_groq_budget_are_refused_before_sending_and_save_nothing()
    {
        var w = new World();
        var fr = w.Fr("FR-001", ArtifactStatus.Approved, "Submit leave", Checked("verified", processing: LongText(1, 4_500)));
        var inner = new RecordingAi(TcResponse);

        var ex = await Assert.ThrowsAsync<AiStageOverBudgetException>(() => w.Tests(Groq(inner)).GenerateAsync(fr.Id));

        Assert.Equal("TestCaseGeneration", ex.Stage);
        Assert.Empty(inner.Calls);
        Assert.Equal(0, w.Db.SaveCount);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.AIExecutions));
    }

    [Fact]
    public async Task A_linked_prompt_that_fits_the_groq_budget_is_sent_whole_exactly_as_built()
    {
        var w = new World();
        var story = w.Story("US-001", ArtifactStatus.Approved);
        for (var i = 1; i <= 3; i++)
        {
            w.Link(story, w.Fr($"FR-00{i}", ArtifactStatus.Approved, $"Requirement {i}", Checked("verified", processing: LongText(i, 300))));
        }

        var unbudgeted = new RecordingAi();
        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(unbudgeted).GenerateAcceptanceCriteriaAsync(story.Id));
        var inner = new RecordingAi();
        await Assert.ThrowsAsync<PromptCaptured>(() => w.Stories(Groq(inner)).GenerateAcceptanceCriteriaAsync(story.Id));

        var sent = Assert.Single(inner.Calls);
        Assert.Equal(Assert.Single(unbudgeted.Calls), sent);
        Assert.True(PromptBudgetReducer.EstimateRequestTokens(sent.System, sent.User) <= MaxRequestTokens - ReservedOutputTokens);
        Assert.All(new[] { "FR-001", "FR-002", "FR-003" }, code => Assert.Contains($"- {code}: ", sent.User));
    }

    private static RequestBudgetAiChatClient Groq(IAiChatClient inner) =>
        new(inner, "Groq", "Ai:Groq:MaxRequestTokens", MaxRequestTokens, ReservedOutputTokens, NullLogger.Instance);

    // Distinct sentences estimated at about `tokens` tokens - no repeated line, so lossless clean-up cannot shrink it.
    private static string LongText(int seed, int tokens)
    {
        var chars = (int)(tokens * PromptBudgetReducer.CharsPerToken);
        var sb = new StringBuilder();
        for (var i = 0; sb.Length < chars; i++)
        {
            sb.Append($"Rule {seed}-{i} states one more distinct processing detail. ");
        }

        return sb.ToString();
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // A payload as RequirementGenerationService saves it: evidence checked, with a verified quote whose
    // text and excerpt label must never reach a prompt.
    private static FunctionalRequirementPayload Checked(
        string status, string[]? numbers = null, string? origin = null, string? inputs = null, string? processing = null) => new()
    {
        Actor = "Employee",
        Preconditions = "Signed in",
        Inputs = inputs,
        Processing = processing ?? "Records the request",
        ExpectedResult = "Request is pending",
        RequirementOrigin = origin,
        DerivationNote = "DERIVATIONSENTINEL",
        SourceReferences = ["notes.pdf#3"],
        Evidence =
        [
            new EvidenceQuotePayload { From = "srs", Quote = QuoteSentinel, Status = "verified", MatchMode = "strict", MatchCount = 1 },
            new EvidenceQuotePayload { From = "excerpt", Quote = QuoteSentinel, Ref = "notes.pdf#3", Status = "verified", MatchCount = 1 }
        ],
        EvidenceStatus = status,
        UnsupportedNumbers = (numbers ?? []).ToList(),
        EvidenceCheck = RequirementEvidence.CheckVersion
    };

    // ---- fixture ----------------------------------------------------------------------------------

    private sealed class World
    {
        public const string StoryPrompt = "Title: Request leave\nPersona: Employee\nValue: Plan time off";

        public InMemoryAiReapDb Db { get; } = new();
        public RequirementSource Source { get; }
        private ICurrentUser User { get; } = new TestCurrentUser("ba-user", "BusinessAnalyst");
        private IProjectAccessService Access { get; } = new AllowAllAccess();

        public World()
        {
            Source = new RequirementSource { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), RawText = "Employees manage their annual leave." };
            Db.RequirementSources.Add(Source);
        }

        public Artifact Add(ArtifactType type, string code, ArtifactStatus status, string title, string dataJson)
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = Source.ProjectId, RequirementSourceId = Source.Id,
                ArtifactType = type, Code = code, Title = title, Status = status, DataJson = dataJson
            };
            Db.Artifacts.Add(artifact);
            return artifact;
        }

        public Artifact Fr(string code, ArtifactStatus status, string title, FunctionalRequirementPayload payload) =>
            Add(ArtifactType.FunctionalRequirement, code, status, title, JsonSerializer.Serialize(payload));

        public Artifact Story(string code, ArtifactStatus status) =>
            Add(ArtifactType.UserStory, code, status, "Request leave",
                JsonSerializer.Serialize(new UserStoryPayload { Persona = "Employee", ValueStatement = "Plan time off" }));

        public void Link(Artifact story, Artifact target) => Db.ArtifactRelationships.Add(new ArtifactRelationship
        {
            Id = Guid.NewGuid(), SourceArtifactId = story.Id, TargetArtifactId = target.Id, RelationshipType = RelationshipType.DerivedFrom,
            SourceArtifact = story, TargetArtifact = target
        });

        public UserStoryService Stories(IAiChatClient ai) => new(ai, Db, User, Access, NullLogger<UserStoryService>.Instance);

        public TestGenerationService Tests(IAiChatClient ai) => new(ai, Db, User, Access, NullLogger<TestGenerationService>.Instance);
    }

    // Records every call; answers with the scripted responses in order, then throws PromptCaptured so a
    // test can inspect the prompt a stage built without that stage saving anything.
    private sealed class RecordingAi(params string[] responses) : IAiChatClient
    {
        public List<(string System, string User)> Calls { get; } = new();
        public string ModelName => "scripted-test-model";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            Calls.Add((systemPrompt, userPrompt));
            return Calls.Count <= responses.Length ? Task.FromResult(responses[Calls.Count - 1]) : throw new PromptCaptured();
        }
    }

    private sealed class PromptCaptured : Exception;

    private sealed class AllowAllAccess : IProjectAccessService
    {
        public Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
