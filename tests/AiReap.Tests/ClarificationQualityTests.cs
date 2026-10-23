using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Agents;
using AiReap.Application.Agents.Implementations;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Knowledge;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Ai;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// Clarification question quality and analysis reuse, through the REAL RequirementAnalysisService over
// InMemoryAiReapDb. Offline: scripted AI responses (ScriptedAiChatClient fails the test on any AI call it
// was not scripted for, which is how "no rerun" is proven), no SQL Server, no network, no provider.
public class ClarificationQualityTests(ITestOutputHelper output)
{
    private const string ClearSrs = """
        # Leave Management

        ## 1. Submitting
        Employees submit leave requests through the portal.

        ## 2. Approval
        Line managers approve or reject leave requests within 2 working days.

        ## 3. Balance
        Each employee has 25 days of annual leave per calendar year.
        A request that exceeds the remaining balance is rejected automatically.
        """;

    private const string AmbiguousSrs = """
        # Leave Management

        ## 1. Submitting
        Employees submit leave requests through the portal.
        The portal must respond quickly when a leave request is submitted.

        ## 2. Cancellation
        Employees may cancel a request up to 24 hours before the leave starts.

        ## 3. Policies
        Requests can be cancelled up to 48 hours before the start date.
        """;

    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };

    // ---- helpers -----------------------------------------------------------------------------

    private static (InMemoryAiReapDb Db, RequirementSource Source) Seed(string rawText)
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "CQ quality project", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = rawText,
            CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        return (db, source);
    }

    private static RequirementAnalysisService Analysis(InMemoryAiReapDb db, IAiChatClient ai, IKnowledgeRetriever? retriever = null) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), retriever ?? new NoDocumentsRetriever(), new BatchModeOff(),
            NullLogger<RequirementAnalysisService>.Instance);

    private static JsonObject Question(string kind, string topic, string question, string reason, params string[] evidence) => new()
    {
        ["kind"] = kind, ["topic"] = topic, ["question"] = question, ["reason"] = reason,
        ["impact"] = "The answer decides how the requirement is implemented and tested.",
        ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e).ToArray())
    };

    private static string Response(params JsonObject[] questions) => new JsonObject
    {
        ["actors"] = new JsonArray("Employee", "Line manager"),
        ["capabilities"] = new JsonArray("Submit leave request", "Approve leave request"),
        ["dataElements"] = new JsonArray("Leave request", "Leave balance"),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(questions.Cast<JsonNode>().ToArray())
    }.ToJsonString();

    private static List<Artifact> Questions(InMemoryAiReapDb db) =>
        InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion).OrderBy(a => a.Code).ToList();

    private static ClarificationQuestionPayload Payload(Artifact a) =>
        JsonSerializer.Deserialize<ClarificationQuestionPayload>(a.DataJson, Lenient)!;

    private static List<AIExecution> Records(InMemoryAiReapDb db) =>
        InMemoryAiReapDb.Rows(db.AIExecutions).Where(e => e.OperationType == RequirementAnalysisRecords.OperationType).ToList();

    private void Report(AnalyzeRequirementResult result)
    {
        foreach (var q in result.ClarificationQuestions)
        {
            output.WriteLine($"ACCEPTED {q.Code}: {q.Data.GetProperty("question").GetString()}");
        }

        foreach (var r in result.RejectedCandidates ?? [])
        {
            output.WriteLine($"REJECTED \"{r.Question}\" -> {string.Join("; ", r.Reasons.Select(x => $"{x.Code}: {x.Message}"))}");
        }
    }

    private sealed class ThrowingRetriever : IKnowledgeRetriever
    {
        public Task<RetrievalResult> RetrieveAsync(Guid projectId, string query, int topK, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("Embedding provider unavailable (simulated).");

        public Task<IReadOnlyList<RetrievedChunk>> LargestCandidatesAsync(Guid projectId, int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
    }

    private sealed class FixedRetriever(params RetrievedChunk[] chunks) : IKnowledgeRetriever
    {
        public List<string> Queries { get; } = [];

        public Task<RetrievalResult> RetrieveAsync(Guid projectId, string query, int topK, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new RetrievalResult(chunks.Take(topK).ToList(), 0, true));
        }

        public Task<IReadOnlyList<RetrievedChunk>> LargestCandidatesAsync(Guid projectId, int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
    }

    // ---- 1. a clear SRS ------------------------------------------------------------------------

    [Fact]
    public async Task A_clear_srs_produces_no_unnecessary_clarification_questions()
    {
        var (db, source) = Seed(ClearSrs);
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "Approver", "Who approves leave requests?",
                "Section 1 does not say who approves a request.", "Employees submit leave requests through the portal."),
            Question("missing", "Other requirements", "Are there any other requirements for the portal?", "Completeness."),
            Question("undefined-rule", "Security", "Should the portal follow security best practices?",
                "Security is not mentioned.", "Employees submit leave requests through the portal.")));

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);
        Report(result);

        Assert.Empty(result.ClarificationQuestions);
        Assert.Empty(Questions(db));
        Assert.Equal(3, result.RejectedCandidates!.Count);
        Assert.Contains(result.RejectedCandidates[0].Reasons, r => r.Code == "answered-in-source");
        Assert.Contains(result.RejectedCandidates[1].Reasons, r => r.Code == "generic");
        Assert.Contains(result.RejectedCandidates[1].Reasons, r => r.Code == "no-evidence");
        Assert.Contains(result.RejectedCandidates[2].Reasons, r => r.Code == "generic");
        // The run is still recorded, so it is not repeated just because it raised nothing.
        Assert.Single(Records(db));
    }

    [Fact]
    public async Task An_analysis_that_raises_no_questions_is_a_valid_result()
    {
        var (db, source) = Seed(ClearSrs);
        var result = await Analysis(db, new ScriptedAiChatClient(Response())).AnalyzeAsync(source.Id);

        Assert.Empty(result.ClarificationQuestions);
        Assert.Empty(result.RejectedCandidates!);
        Assert.Equal(AnalysisCoverageReport.Complete, result.Coverage!.Status);
        Assert.Empty(result.StaleReasons!);
    }

    // ---- 2. a genuinely ambiguous requirement --------------------------------------------------

    [Fact]
    public async Task A_genuinely_ambiguous_requirement_produces_a_relevant_grounded_question()
    {
        var (db, source) = Seed(AmbiguousSrs);
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?",
                "\"Quickly\" is not measurable.", "The portal must respond quickly when a leave request is submitted.")));

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);
        Report(result);

        var saved = Assert.Single(Questions(db));
        var payload = Payload(saved);
        Assert.Equal("ambiguous", payload.Kind);
        Assert.Equal("Open", payload.ClarificationStatus);
        Assert.False(string.IsNullOrWhiteSpace(payload.Impact));
        var evidence = Assert.Single(payload.SourceEvidence!);
        Assert.True(evidence.ExcerptIsQuote);
        Assert.Equal("The portal must respond quickly when a leave request is submitted.", evidence.Excerpt);
        Assert.Equal(4, evidence.Line); // 0-based line in the source
        Assert.Equal(["1. Submitting"], payload.RequirementRefs);
    }

    // ---- 3. a contradiction ----------------------------------------------------------------------

    [Fact]
    public async Task A_contradiction_produces_a_question_grounded_in_both_conflicting_statements()
    {
        var (db, source) = Seed(AmbiguousSrs);
        var ai = new ScriptedAiChatClient(Response(
            Question("contradiction", "Cancellation deadline", "Is the cancellation deadline 24 hours or 48 hours before the leave starts?",
                "Sections 2 and 3 give different deadlines.",
                "cancel a request up to 24 hours before the leave starts", "Requests can be cancelled up to 48 hours before the start date"),
            Question("contradiction", "Cancellation (one side)", "Which cancellation deadline applies to requests?",
                "The deadlines differ.", "cancel a request up to 24 hours before the leave starts")));

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);
        Report(result);

        var saved = Assert.Single(Questions(db));
        var payload = Payload(saved);
        Assert.Equal("contradiction", payload.Kind);
        Assert.Equal(2, payload.SourceEvidence!.Count);
        Assert.NotEqual(payload.SourceEvidence[0].Line, payload.SourceEvidence[1].Line);
        Assert.Equal(["2. Cancellation", "3. Policies"], payload.RequirementRefs);

        var rejected = Assert.Single(result.RejectedCandidates!);
        Assert.Contains(rejected.Reasons, r => r.Code == "contradiction-needs-both-statements");
    }

    // ---- 4. answered in another section ----------------------------------------------------------

    [Fact]
    public async Task Information_answered_in_another_section_does_not_produce_a_question()
    {
        var (db, source) = Seed(ClearSrs);
        // What a part-by-part analysis tends to ask: section 1 alone does not say who approves.
        var ai = new ScriptedAiChatClient(Response(
            Question("missing", "Approval", "Who approves or rejects leave requests?",
                "Section 1 names no approver.", "Employees submit leave requests through the portal."),
            Question("missing", "Allowance", "How many days of annual leave does each employee have?",
                "The allowance is not given in section 1.", "Employees submit leave requests through the portal.")));

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);
        Report(result);

        Assert.Empty(Questions(db));
        Assert.All(result.RejectedCandidates!, r => Assert.Contains(r.Reasons, x => x.Code == "answered-in-source"));
        Assert.Contains("2. Approval", result.RejectedCandidates![0].Reasons.Single(x => x.Code == "answered-in-source").Location);
        Assert.Contains("3. Balance", result.RejectedCandidates[1].Reasons.Single(x => x.Code == "answered-in-source").Location);
    }

    // ---- 5. duplicates in one response -----------------------------------------------------------

    [Fact]
    public async Task Duplicate_and_paraphrased_questions_are_persisted_once()
    {
        var (db, source) = Seed(AmbiguousSrs);
        const string quote = "The portal must respond quickly when a leave request is submitted.";
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?", "Not measurable.", quote),
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted ?", "Not measurable.", quote),
            Question("ambiguous", "Speed", "How quickly must the portal respond when a leave request is submitted?", "Not measurable.", quote)));

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);
        Report(result);

        Assert.Single(Questions(db));
        Assert.Equal(2, result.RejectedCandidates!.Count);
        Assert.All(result.RejectedCandidates, r => Assert.Contains(r.Reasons, x => x.Code == "duplicate"));
        Assert.Equal(2, result.SkippedDuplicates);
        Assert.Single(InMemoryAiReapDb.Rows(db.ArtifactFingerprints));
    }

    // ---- 6. reopening does not rerun --------------------------------------------------------------

    [Fact]
    public async Task Reopening_a_completed_analysis_returns_the_stored_result_without_an_ai_call()
    {
        var (db, source) = Seed(AmbiguousSrs);
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?",
                "Not measurable.", "The portal must respond quickly when a leave request is submitted.")));

        var first = await Analysis(db, ai).AnalyzeAsync(source.Id);
        // A later request (new service instance), the read endpoint, and Analyze again: none may call the AI
        // - the scripted client has no response left and would throw.
        var viewed = await Analysis(db, ai).GetLatestAsync(source.Id);
        var again = await Analysis(db, ai).AnalyzeAsync(source.Id);

        Assert.Single(ai.Prompts);
        Assert.False(first.Reused);
        Assert.True(viewed!.Reused);
        Assert.True(again.Reused);
        Assert.Empty(again.ClarificationQuestions); // nothing new was created by the reuse
        Assert.Equal(first.Actors, again.Actors);
        Assert.Equal(first.AnalyzedAt, again.AnalyzedAt);
        Assert.False(again.Stale);
        Assert.Single(Questions(db));
        Assert.Single(Records(db));
    }

    [Fact]
    public async Task The_requirements_agent_reuses_an_analysis_that_raised_no_questions()
    {
        var (db, source) = Seed(ClearSrs);
        var ai = new ScriptedAiChatClient(Response()); // one AI response only
        var agent = new RequirementsAgent(Analysis(db, ai), db);
        var context = new AgentContext(source.ProjectId, source.Id);

        var first = await agent.RunAsync(context, CancellationToken.None);
        var second = await new RequirementsAgent(Analysis(db, ai), db).RunAsync(context, CancellationToken.None);

        Assert.Single(ai.Prompts);
        Assert.StartsWith("Analysed the requirement", first.Summary);
        Assert.StartsWith("Reused the stored analysis", second.Summary);
        Assert.Equal(0, second.Metrics["clarificationQuestions"]);
    }

    [Fact]
    public async Task A_source_with_no_analysis_reads_as_never_analysed()
    {
        var (db, source) = Seed(ClearSrs);
        Assert.Null(await Analysis(db, new ScriptedAiChatClient()).GetLatestAsync(source.Id));
    }

    [Fact]
    public async Task A_source_analysed_before_records_were_kept_is_reused_and_flagged_not_rerun()
    {
        var (db, source) = Seed(ClearSrs);
        db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(), ProjectId = source.ProjectId, OperationType = "RequirementAnalysis", UserId = "u",
            Timestamp = DateTime.UtcNow.AddDays(-3), Model = "older-model", PromptTemplateVersion = "RequirementAnalysis-v2",
            InputReference = source.Id.ToString(), OutputJson = "{}"
        });

        var result = await Analysis(db, new ScriptedAiChatClient()).AnalyzeAsync(source.Id);

        Assert.True(result.Reused);
        Assert.True(result.Stale);
        Assert.Contains("before analysis results were recorded", Assert.Single(result.StaleReasons!));
    }

    // ---- 7. explicit regeneration reconciles -------------------------------------------------------

    [Fact]
    public async Task Explicit_regeneration_adds_only_new_questions_and_never_changes_saved_ones()
    {
        var (db, source) = Seed(AmbiguousSrs);
        const string quick = "The portal must respond quickly when a leave request is submitted.";
        var ai = new ScriptedAiChatClient(
            Response(
                Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?", "Not measurable.", quick),
                Question("contradiction", "Cancellation deadline", "Is the cancellation deadline 24 hours or 48 hours before the leave starts?",
                    "Two deadlines.", "cancel a request up to 24 hours before the leave starts", "Requests can be cancelled up to 48 hours before the start date")),
            // The regeneration: both earlier questions paraphrased, and one genuinely new issue.
            Response(
                Question("ambiguous", "Speed", "How quickly must the portal respond when a leave request is submitted?", "Not measurable.", quick),
                Question("contradiction", "Deadline conflict", "Which cancellation deadline applies, 24 hours or 48 hours?",
                    "Two deadlines.", "cancel a request up to 24 hours before the leave starts", "Requests can be cancelled up to 48 hours before the start date"),
                Question("missing", "Portal", "Which portal or channel do employees use to submit leave requests?",
                    "The portal is not identified.", "Employees submit leave requests through the portal.")));

        await Analysis(db, ai).AnalyzeAsync(source.Id);
        var saved = Questions(db);
        Assert.Equal(2, saved.Count);

        // A person answers the first question.
        var answered = Payload(saved[0]);
        answered.ClarificationStatus = "Answered";
        answered.Answer = "Within 2 seconds.";
        saved[0].DataJson = JsonSerializer.Serialize(answered);
        var before = saved.ToDictionary(a => a.Id, a => (a.DataJson, a.CurrentVersion, a.Status));

        var regenerated = await Analysis(db, ai).AnalyzeAsync(source.Id, regenerate: true);
        Report(regenerated);

        Assert.Equal(2, ai.Prompts.Count);
        Assert.False(regenerated.Reused);
        var added = Assert.Single(regenerated.ClarificationQuestions);
        Assert.Equal("Portal", added.Title);
        Assert.Equal(3, Questions(db).Count);
        Assert.Contains(regenerated.RejectedCandidates!, r => r.Reasons.Any(x => x.Code == "already-answered" && x.RelatedCode == saved[0].Code));
        Assert.Contains(regenerated.RejectedCandidates!, r => r.Reasons.Any(x => x.Code == "duplicate" && x.RelatedCode == saved[1].Code));
        Assert.Equal(new[] { saved[0].Code, saved[1].Code }.OrderBy(c => c), regenerated.Reconciliation!.RaisedAgain.OrderBy(c => c));
        Assert.Empty(regenerated.Reconciliation.NotRaisedAgain);
        foreach (var artifact in saved)
        {
            Assert.Equal(before[artifact.Id], (artifact.DataJson, artifact.CurrentVersion, artifact.Status)); // untouched
        }

        Assert.Equal(2, Records(db).Count);
        Assert.True((await Analysis(db, ai).GetLatestAsync(source.Id))!.AnalyzedAt >= regenerated.AnalyzedAt);
    }

    [Fact]
    public async Task Regeneration_reports_open_questions_it_did_not_raise_again_without_removing_them()
    {
        var (db, source) = Seed(AmbiguousSrs);
        const string quick = "The portal must respond quickly when a leave request is submitted.";
        var ai = new ScriptedAiChatClient(
            Response(Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?", "Not measurable.", quick)),
            Response());

        await Analysis(db, ai).AnalyzeAsync(source.Id);
        var regenerated = await Analysis(db, ai).AnalyzeAsync(source.Id, regenerate: true);

        Assert.Equal([Questions(db).Single().Code], regenerated.Reconciliation!.NotRaisedAgain);
        Assert.Single(Questions(db));
    }

    // ---- 8. an updated SRS ------------------------------------------------------------------------

    [Fact]
    public async Task A_changed_source_marks_the_stored_analysis_stale_until_it_is_regenerated()
    {
        var (db, source) = Seed(ClearSrs);
        var ai = new ScriptedAiChatClient(Response(), Response());
        await Analysis(db, ai).AnalyzeAsync(source.Id);

        source.RawText = ClearSrs + "\nManagers may delegate approval to a deputy.";
        var stale = await Analysis(db, ai).AnalyzeAsync(source.Id);

        Assert.Single(ai.Prompts); // flagged, not silently re-sent
        Assert.True(stale.Reused);
        Assert.Contains(stale.StaleReasons!, r => r.Contains("source text has changed"));

        var fresh = await Analysis(db, ai).AnalyzeAsync(source.Id, regenerate: true);
        Assert.Equal(2, ai.Prompts.Count);
        Assert.Contains("delegate approval", ai.Prompts[1].User);
        Assert.False(fresh.Stale);
        Assert.False((await Analysis(db, ai).GetLatestAsync(source.Id))!.Stale);
    }

    [Fact]
    public async Task A_record_from_an_older_analysis_version_is_reported_stale()
    {
        var (db, source) = Seed(ClearSrs);
        await Analysis(db, new ScriptedAiChatClient(Response())).AnalyzeAsync(source.Id);
        var row = Records(db).Single();
        var record = JsonSerializer.Deserialize<RequirementAnalysisRecord>(row.OutputJson)!;
        record.AnalysisVersion = "RequirementAnalysis-v2+CqValidation-v0";
        row.OutputJson = JsonSerializer.Serialize(record);

        var result = await Analysis(db, new ScriptedAiChatClient()).AnalyzeAsync(source.Id);

        Assert.Contains(result.StaleReasons!, r => r.Contains("analysis rules have changed"));
    }

    // ---- 9 / 10. coverage --------------------------------------------------------------------------

    private static string LargeSrsWithTablesAndAppendix()
    {
        var sb = new System.Text.StringBuilder("# Inventory System\n\n");
        for (var s = 1; s <= 60; s++)
        {
            sb.Append($"## {s}. Area {s}\n");
            for (var l = 1; l <= 8; l++) sb.Append($"REQ-{s}.{l}: The system shall record item {s}-{l} with its location and owner.\n");
            sb.Append($"| Field | Rule |\n|---|---|\n| qty-{s} | must be at least 0 |\n\n");
        }

        sb.Append("## Appendix A - Constraints\nAll stock moves are logged for 7 years.\n\n## Appendix B - Business rules\nTransfers above 100 units need a second approver.\n");
        return sb.ToString();
    }

    [Fact]
    public void A_large_split_source_accounts_for_every_section_tables_and_appendices_included()
    {
        var source = LargeSrsWithTablesAndAppendix();
        var parts = SourceChunker.Split(source, 1_500);
        Assert.True(parts.Count > 3, $"expected several parts, got {parts.Count}");

        var report = AnalysisCoverage.Assess(source, parts.Select(p => (string?)p).ToList());

        Assert.Equal(AnalysisCoverageReport.Complete, report.Status);
        Assert.Equal(parts.Count, report.PartCount);
        Assert.Equal(report.SectionCount, report.SectionsCovered);
        Assert.Equal(report.LineCount, report.LinesCovered);
        Assert.Contains(report.Parts.SelectMany(p => p.Sections), s => s.StartsWith("Appendix B"));
        // Every section is attributed to exactly one part (the one its heading went with).
        Assert.Equal(report.SectionCount, report.Parts.Sum(p => p.Sections.Count));
    }

    [Fact]
    public void Incomplete_processing_is_reported_with_the_sections_left_out()
    {
        var source = LargeSrsWithTablesAndAppendix();
        var parts = SourceChunker.Split(source, 1_500).ToList();
        var dropped = parts[^1];
        parts.RemoveAt(parts.Count - 1);

        var report = AnalysisCoverage.Assess(source, parts.Select(p => (string?)p).ToList());

        Assert.Equal(AnalysisCoverageReport.Incomplete, report.Status);
        Assert.Contains("Appendix B - Business rules", report.MissingSections);
        Assert.True(report.LinesCovered < report.LineCount);
        Assert.Contains("not sent to the AI", report.Note);
        Assert.Contains("Transfers above 100 units", dropped);

        var record = new RequirementAnalysisRecord
        {
            AnalysisVersion = RequirementAnalysisRecords.AnalysisVersion,
            SourceHash = RequirementAnalysisRecords.SourceHash(source),
            Coverage = report
        };
        Assert.Contains("did not cover the whole source",
            Assert.Single(RequirementAnalysisRecords.StaleReasons(record, new RequirementSource { RawText = source })));
    }

    [Fact]
    public void Coverage_is_not_claimed_when_the_part_texts_are_unknown()
    {
        var report = AnalysisCoverage.Assess(ClearSrs, [null, null]);

        Assert.Equal(AnalysisCoverageReport.Unverified, report.Status);
        Assert.Equal(0, report.SectionsCovered);
    }

    // ---- 11. evidence -----------------------------------------------------------------------------

    [Fact]
    public async Task Missing_invalid_or_unrelated_evidence_causes_rejection()
    {
        var (db, source) = Seed(AmbiguousSrs);
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "No evidence", "What response time is acceptable for submitted requests?", "Not measurable."),
            Question("ambiguous", "Invented", "What response time is acceptable for submitted requests?", "Not measurable.",
                "The portal must answer every request within one second."),
            Question("contradiction", "Altered number", "Is the cancellation deadline 12 hours or 48 hours?", "Two deadlines.",
                "cancel a request up to 12 hours before the leave starts", "Requests can be cancelled up to 48 hours before the start date"),
            Question("ambiguous", "Too short", "What does quickly mean for submissions?", "Not measurable.", "quickly"),
            Question("missing", "Colours", "Which colours should the confirmation screen use?", "Not specified.",
                "Employees submit leave requests through the portal."),
            Question("ambiguous", "Two questions", "How fast must it respond? And who measures it?", "Not measurable.",
                "The portal must respond quickly when a leave request is submitted."),
            Question("guess", "Bad kind", "What response time counts as quick for the portal?", "Not measurable.",
                "The portal must respond quickly when a leave request is submitted.")));
        // And one with no impact at all.
        var noImpact = JsonNode.Parse(Response(Question("ambiguous", "No impact", "What response time counts as quick for the portal?",
            "Not measurable.", "The portal must respond quickly when a leave request is submitted.")))!;
        noImpact["missingInformation"]![0]!.AsObject().Remove("impact");

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);
        Report(result);
        var second = await Analysis(db, new ScriptedAiChatClient(noImpact.ToJsonString())).AnalyzeAsync(source.Id, regenerate: true);

        Assert.Empty(Questions(db));
        string[] Codes(string topic) => result.RejectedCandidates!.Single(r => r.Topic == topic).Reasons.Select(r => r.Code).ToArray();
        Assert.Contains("no-evidence", Codes("No evidence"));
        Assert.Contains("no-evidence", Codes("Invented"));
        Assert.Contains("contradiction-needs-both-statements", Codes("Altered number")); // only the 48-hour quote is real
        Assert.Contains("no-evidence", Codes("Too short"));
        Assert.Contains("unrelated", Codes("Colours"));
        Assert.Contains("multiple-questions", Codes("Two questions"));
        Assert.Contains("invalid-kind", Codes("Bad kind"));
        Assert.Contains(Assert.Single(second.RejectedCandidates!).Reasons, r => r.Code == "missing-fields");
    }

    [Fact]
    public async Task A_whole_one_line_source_can_be_quoted_as_evidence()
    {
        var (db, source) = Seed("Leave requests.");
        var ai = new ScriptedAiChatClient(Response(
            Question("missing", "Scope", "Who submits and who decides leave requests?", "The source names no actors.", "Leave requests.")));

        await Analysis(db, ai).AnalyzeAsync(source.Id);

        Assert.Single(Questions(db));
    }

    // ---- 12. retrieval ----------------------------------------------------------------------------

    [Fact]
    public async Task A_retrieval_failure_is_reported_and_the_questions_are_still_saved()
    {
        var (db, source) = Seed(AmbiguousSrs);
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?",
                "Not measurable.", "The portal must respond quickly when a leave request is submitted.")));

        var result = await Analysis(db, ai, new ThrowingRetriever()).AnalyzeAsync(source.Id);

        Assert.Equal("failed", result.Retrieval!.ProjectDocuments);
        Assert.Contains("Embedding provider unavailable", result.Retrieval.Error);
        Assert.Null(Payload(Assert.Single(Questions(db))).RelatedDocuments);
    }

    [Fact]
    public async Task Project_document_retrieval_runs_per_accepted_question_and_attaches_relevant_passages()
    {
        var (db, source) = Seed(AmbiguousSrs);
        var retriever = new FixedRetriever(
            new RetrievedChunk("sla.pdf", 2, "Portal pages must render within 2 seconds for 95% of requests.", 0.82f),
            new RetrievedChunk("misc.docx", 9, "Cafeteria opening hours.", 0.2f));
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?",
                "Not measurable.", "The portal must respond quickly when a leave request is submitted."),
            Question("missing", "Colours", "Which colours should the confirmation screen use?", "Not specified.")));

        var result = await Analysis(db, ai, retriever).AnalyzeAsync(source.Id);

        // Only the accepted question is searched for; the rejected one never reaches retrieval.
        var query = Assert.Single(retriever.Queries);
        Assert.Contains("What response time counts as quick", query);
        Assert.Equal("used", result.Retrieval!.ProjectDocuments);
        Assert.Equal("lexical", result.Retrieval.SourcePassages);
        Assert.Equal(1, result.Retrieval.Queries);
        Assert.Equal(1, result.Retrieval.PassagesAttached);
        var related = Assert.Single(Payload(Assert.Single(Questions(db))).RelatedDocuments!);
        Assert.Equal("sla.pdf#2", related.Reference);
    }

    [Fact]
    public async Task Retrieval_reports_when_the_project_has_no_documents()
    {
        var (db, source) = Seed(AmbiguousSrs);
        var ai = new ScriptedAiChatClient(Response(
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?",
                "Not measurable.", "The portal must respond quickly when a leave request is submitted.")));

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);

        Assert.Equal("no-documents", result.Retrieval!.ProjectDocuments);
        Assert.Equal(0, result.Retrieval.Queries);
    }

    // ---- server-owned evidence ----------------------------------------------------------------------

    [Fact]
    public async Task Evidence_is_withheld_from_restricted_callers_and_kept_through_human_edits()
    {
        var (db, source) = Seed(AmbiguousSrs);
        await Analysis(db, new ScriptedAiChatClient(Response(
            Question("ambiguous", "Response time", "What response time counts as quick when a leave request is submitted?",
                "Not measurable.", "The portal must respond quickly when a leave request is submitted.")))).AnalyzeAsync(source.Id);
        var json = Questions(db).Single().DataJson;

        var redacted = JsonNode.Parse(RequirementEvidenceFields.RedactForRestrictedCaller(json))!.AsObject();
        Assert.False(redacted.ContainsKey(nameof(ClarificationQuestionPayload.SourceEvidence)));
        Assert.True(redacted.ContainsKey(nameof(ClarificationQuestionPayload.Question)));

        var edited = new JsonObject { ["question"] = "Edited?", ["sourceEvidence"] = new JsonArray(), ["requirementRefs"] = new JsonArray("forged") };
        var kept = ClarificationSourceReferences.KeepOnEdit(json, edited);
        Assert.Single(kept[nameof(ClarificationQuestionPayload.SourceEvidence)]!.AsArray());
        Assert.Equal("1. Submitting", kept[nameof(ClarificationQuestionPayload.RequirementRefs)]![0]!.GetValue<string>());
    }

    // ---- the development stub ------------------------------------------------------------------------

    [Theory]
    [InlineData("Leave requests.", 2)]
    [InlineData("Employees submit leave requests; managers approve.", 2)]
    [InlineData("Employees submit leave requests through the portal. Managers approve them within two days.", 2)]
    public async Task The_development_stub_raises_grounded_questions_that_pass_validation(string rawText, int expected)
    {
        var (db, source) = Seed(rawText);

        var result = await Analysis(db, new StubAiChatClient()).AnalyzeAsync(source.Id);
        Report(result);

        Assert.Equal(expected, result.ClarificationQuestions.Count);
        Assert.All(Questions(db), q => Assert.NotEmpty(Payload(q).SourceEvidence!));
    }

    [Fact]
    public void The_analyze_response_keeps_its_existing_fields_first_and_appends_the_new_ones()
    {
        var json = JsonSerializer.SerializeToNode(new AnalyzeRequirementResult([], [], [], [], []),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();

        // The same list CompletenessFlagsTests asserts over HTTP (a SQL-backed test).
        Assert.Equal(["actors", "capabilities", "dataElements", "notes", "clarificationQuestions", "skippedDuplicates", "completeness",
                "reused", "staleReasons", "analysisVersion", "analyzedAt", "coverage", "rejectedCandidates", "retrieval", "reconciliation", "stale"],
            json.Select(kv => kv.Key));
    }

    // ---- before / after on the golden fixture --------------------------------------------------------

    [Fact]
    public async Task The_poor_golden_candidate_is_rejected_entirely_with_reasons()
    {
        var source = GoldenFixture.Source(GoldenFixture.ClinicBooking);
        var (db, seeded) = Seed(source);
        var poor = GoldenFixture.Candidate("analysis-poor");
        var ai = new ScriptedAiChatClient(Response(poor.Clarifications
            .Select(c => new JsonObject { ["topic"] = c.Topic, ["question"] = c.Question, ["reason"] = c.Reason })
            .ToArray()));

        var result = await Analysis(db, ai).AnalyzeAsync(seeded.Id);
        Report(result);

        // Before this change all of these were saved except the exact repeat (3 questions); now none is.
        Assert.Empty(Questions(db));
        Assert.Equal(poor.Clarifications.Count, result.RejectedCandidates!.Count);
        Assert.All(result.RejectedCandidates, r => Assert.Contains(r.Reasons, x => x.Code == "no-evidence"));
    }
}
