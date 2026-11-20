using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Ai;
using Xunit;
using static AiReap.Tests.ClaudeLargeSrsPipelineTests;

namespace AiReap.Tests;

// Staged requirement generation (AiOutputStaging) through the REAL RequirementGenerationService and the
// Claude chain exactly as AddInfrastructure builds it, over a fake HTTP transport and InMemoryAiReapDb.
// Offline: no SQL Server, no network, no AI provider. The fake model is deterministic and faithful: for
// every requirement line ("FR-001: ...") in the text it was SENT, it returns one requirement citing that
// ID and quoting the line verbatim - so coverage, evidence and traceability checks are meaningful, and a
// requirement can only appear if its source line actually reached the model.
public class ClaudeStagedGenerationTests
{
    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex RequirementLine = new(@"(?m)^((?:N?FR)-\d{3}): (.+)$");
    private static readonly Regex AuditNote = new(@"(?m)^(Note: booking audit also applies as described in FR-001\.)$");

    // count requirement lines in sections of perSection; extra lines can be placed after a given line number.
    internal static string Srs(int count, int perSection = 10, IReadOnlyDictionary<int, string>? after = null)
    {
        var text = new StringBuilder("# Clinic booking SRS\n\n");
        for (var i = 1; i <= count; i++)
        {
            if ((i - 1) % perSection == 0) text.Append($"\n## 3.{(i - 1) / perSection + 1} Booking area {(i - 1) / perSection + 1}\n\n");
            text.Append($"FR-{i:000}: The system shall record booking event {i} with its timestamp, room and clinician for audit.\n");
            if (after is not null && after.TryGetValue(i, out var extra)) text.Append(extra).Append('\n');
        }

        return text.ToString();
    }

    private static JsonObject Fr(string id, string text, string? title = null) => new()
    {
        ["title"] = title ?? $"Requirement {id}", ["actor"] = "Receptionist", ["priority"] = "High",
        ["preconditions"] = null, ["inputs"] = null, ["processing"] = null, ["expectedResult"] = text,
        ["dependencies"] = new JsonArray(), ["businessObjectiveCodes"] = new JsonArray(), ["sourceReferences"] = new JsonArray(),
        ["sourceIds"] = new JsonArray(JsonValue.Create(id)), ["dependsOnSourceIds"] = new JsonArray(),
        ["requirementOrigin"] = "explicit", ["derivationNote"] = null, ["assumedClarifications"] = new JsonArray(),
        ["evidence"] = new JsonArray(new JsonObject { ["from"] = "srs", ["quote"] = text, ["sourceId"] = id, ["ref"] = null })
    };

    private static JsonObject Nfr(string id, string text, string title, string category, string target) => new()
    {
        ["title"] = title, ["category"] = category, ["description"] = text, ["targetValue"] = target, ["targetOrigin"] = "stated",
        ["sourceIds"] = new JsonArray(JsonValue.Create(id)), ["requirementOrigin"] = "explicit", ["derivationNote"] = null,
        ["assumedClarifications"] = new JsonArray(),
        ["evidence"] = new JsonArray(new JsonObject { ["from"] = "srs", ["quote"] = text, ["sourceId"] = id, ["ref"] = null })
    };

    // The faithful model's answer for one prompt.
    internal static string Faithful(string prompt, Action<string, JsonArray, JsonArray>? extra = null)
    {
        var frs = new JsonArray();
        var nfrs = new JsonArray();
        foreach (Match m in RequirementLine.Matches(prompt))
        {
            var (id, text) = (m.Groups[1].Value, m.Groups[2].Value);
            if (id.StartsWith("NFR", StringComparison.Ordinal))
            {
                var target = Regex.Match(text, @"\d+(?:\.\d+)?%").Value;
                nfrs.Add(Nfr(id, $"{id}: {text}", "Booking service availability", "Availability", target));
            }
            else
            {
                frs.Add(Fr(id, text));
            }
        }

        extra?.Invoke(prompt, frs, nfrs);
        return new JsonObject { ["functionalRequirements"] = frs, ["nonFunctionalRequirements"] = nfrs }.ToJsonString();
    }

    private static int Ids(string prompt) => RequirementLine.Matches(prompt).Count;

    private static List<T> Payloads<T>(InMemoryAiReapDb db, ArtifactType type) =>
        InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == type)
            .Select(a => JsonSerializer.Deserialize<T>(a.DataJson, Lenient)!).ToList();

    // ---- small sources are unchanged -------------------------------------------------------------

    [Fact]
    public async Task A_small_srs_is_still_generated_in_one_whole_request()
    {
        var srs = Srs(20);
        await using var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r)))));
        var (db, source) = Seed(srs);

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        Assert.Single(claude.MessageRequests);
        Assert.Equal(1, result.Completeness!.PartCount);
        Assert.Null(result.Completeness.CrossPartSharedSourceIds);
        Assert.Equal(20, result.FunctionalRequirements.Count);
    }

    // ---- proactive staging --------------------------------------------------------------------

    [Fact]
    public async Task A_large_srs_is_generated_in_bounded_parts_with_every_requirement_traced_and_quoted()
    {
        var srs = Srs(400);
        Assert.True(PromptBudgetReducer.EstimateTokens(srs) > 6000);
        await using var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r)))));
        var (db, source) = Seed(srs);

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        var requests = claude.MessageRequests;
        var staging = AiRouteInspector.Inspect(claude.Client).Primary!.OutputStaging!;
        var planned = ChunkPlanner.PlanStaged(SystemPrompt(requests[0]), srs, string.Empty, GenerationSupport.SharedContext.None, staging);
        Assert.Equal(ChunkPlanOutcome.Split, planned.Outcome);
        Assert.Equal(planned.Chunks.Count, requests.Count);      // the dry-run plan's part count is what was sent
        Assert.True(requests.Count > 1);
        Assert.All(requests, r => Assert.True(
            PromptBudgetReducer.EstimateTokens(UserPrompt(r)) <= staging.MaxPartSourceTokens + 200, "part over the staged size"));

        // Every requirement line reached the model exactly once and became exactly one requirement.
        var sentIds = requests.SelectMany(r => RequirementLine.Matches(UserPrompt(r)).Select(m => m.Groups[1].Value)).ToList();
        Assert.Equal(400, sentIds.Count);
        Assert.Equal(400, sentIds.Distinct().Count());
        Assert.Equal(400, result.FunctionalRequirements.Count);
        Assert.Equal(SourceIdCoverage.Complete, result.SourceIdCoverage!.Status);

        var frs = Payloads<FunctionalRequirementPayload>(db, ArtifactType.FunctionalRequirement);
        Assert.All(frs, p => Assert.Single(p.SourceIds));
        Assert.All(frs, p => Assert.Equal("verified", p.EvidenceStatus)); // the original wording, quoted verbatim
        Assert.Equal(RequirementCompleteness.Split, result.Completeness!.ContextReduction);
        Assert.Equal(requests.Count, result.Completeness.PartCount);
        Assert.Empty(result.Completeness.CrossPartSharedSourceIds!);
        Assert.Equal(0, result.Completeness.CrossPartTargetConflicts);
        Assert.Equal(["claude-sonnet-5-5"], result.Completeness.Models);
    }

    [Fact]
    public async Task A_source_needing_more_staged_parts_than_allowed_is_refused_before_anything_is_sent_or_saved()
    {
        var settings = new Dictionary<string, string?> { ["Ai:Anthropic:MaxGenerationParts"] = "2" };
        await using var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r)))), settings);
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiChunkLimitExceededException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.Equal("Ai:Anthropic:MaxGenerationParts", ex.MaxPartsSetting);
        Assert.Contains("Nothing was sent and nothing was saved", ex.Message);
        Assert.Empty(claude.Handler.Requests);
        Assert.Equal(0, db.SaveCount);
    }

    // ---- reactive re-splitting after truncation ------------------------------------------------

    // The fake model's output limit: a prompt with more requirement lines than this is cut off.
    private static FakeHandlerWithLimit TruncatingAbove(int maxIds, Action<string, JsonArray, JsonArray>? extra = null) =>
        new(maxIds, extra);

    private sealed class FakeHandlerWithLimit
    {
        public FakeHandlerWithLimit(int maxIds, Action<string, JsonArray, JsonArray>? extra)
        {
            Handler = Answering(r =>
            {
                var prompt = UserPrompt(r);
                if (Ids(prompt) > maxIds)
                {
                    Truncated.Add(prompt);
                    return Ok("{\"functionalRequirements\": [{\"title\": \"Requirement FR-0", "max_tokens");
                }

                return Ok(Faithful(prompt, extra));
            });
        }

        public AnthropicAiChatClientTests.FakeHandler Handler { get; }
        public List<string> Truncated { get; } = new();
    }

    [Fact]
    public async Task A_part_whose_answer_is_cut_off_is_split_again_and_no_truncated_output_is_used()
    {
        var settings = new Dictionary<string, string?> { ["Ai:Anthropic:GenerationPartSourceTokens"] = "12000" };
        var fake = TruncatingAbove(150);
        await using var claude = new ClaudeStack(fake.Handler, settings);
        var (db, source) = Seed(Srs(400));

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        Assert.NotEmpty(fake.Truncated);
        var answered = claude.MessageUserPrompts.Where(p => Ids(p) <= 150).ToList();
        Assert.Equal(claude.MessageRequests.Count, answered.Count + fake.Truncated.Count);
        // Every ID in a truncated prompt was later sent again in a smaller part that was answered.
        var answeredIds = answered.SelectMany(p => RequirementLine.Matches(p).Select(m => m.Groups[1].Value)).ToList();
        Assert.Equal(400, answeredIds.Distinct().Count());
        Assert.Equal(answeredIds.Count, answeredIds.Distinct().Count()); // still exactly once
        Assert.Equal(400, result.FunctionalRequirements.Count);
        Assert.Equal(answered.Count, result.Completeness!.PartCount);
        Assert.Equal(SourceIdCoverage.Complete, result.SourceIdCoverage!.Status);
    }

    [Fact]
    public async Task A_whole_source_answer_that_is_cut_off_is_regenerated_in_halves()
    {
        var srs = Srs(140);
        Assert.True(PromptBudgetReducer.EstimateTokens(srs) <= 6000); // goes whole first
        var fake = TruncatingAbove(100);
        await using var claude = new ClaudeStack(fake.Handler);
        var (db, source) = Seed(srs);

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        Assert.Single(fake.Truncated);                              // only the whole request was cut off
        Assert.True(result.Completeness!.PartCount >= 2);
        Assert.Equal(1 + result.Completeness.PartCount, claude.MessageRequests.Count); // whole, then the halves
        Assert.Equal(140, result.FunctionalRequirements.Count);
    }

    [Fact]
    public async Task Re_splitting_beyond_the_part_limit_fails_honestly_after_requests_were_sent_and_saves_nothing()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Anthropic:GenerationPartSourceTokens"] = "12000", ["Ai:Anthropic:MaxGenerationParts"] = "3"
        };
        var fake = TruncatingAbove(150);
        await using var claude = new ClaudeStack(fake.Handler, settings);
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiOutputTruncatedException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.Contains("Ai:Anthropic:MaxGenerationParts=3", ex.Message);
        Assert.Contains($"at least {claude.MessageRequests.Count} AI request(s) were sent", ex.Message);
        Assert.DoesNotContain("Nothing was sent", ex.Message);
        Assert.Equal(0, db.SaveCount);
        Assert.Empty(InMemoryAiReapDb.Rows(db.Artifacts));
    }

    // ---- consolidation checks ------------------------------------------------------------------

    [Fact]
    public async Task A_part_that_defines_requirement_ids_but_returns_nothing_stops_the_run_with_nothing_saved()
    {
        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            return Ok(prompt.Contains("FR-250:", StringComparison.Ordinal) ? EmptyAnswer : Faithful(prompt));
        }));
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.Contains("defines requirement IDs", ex.Message);
        Assert.Contains("contained no requirements", ex.Message);
        Assert.False(ex.Repairable);
        Assert.Contains("FR-250", Assert.Single(ex.Parts).MissingIds);
        Assert.Equal(1 + claude.MessageUserPrompts.FindIndex(p => p.Contains("FR-250:", StringComparison.Ordinal)),
            ex.Data[GenerationSupport.FailedPartKey]);
        Assert.Equal(claude.MessageRequests.Count, ex.RequestsSent);
        Assert.Equal(0, db.SaveCount);
        Assert.Empty(InMemoryAiReapDb.Rows(db.Artifacts));
    }

    // ---- H1: a part that returns nothing (StagedPartCoverage) -------------------------------------

    private const string EmptyAnswer = "{\"functionalRequirements\":[],\"nonFunctionalRequirements\":[]}";

    // Small parts, so reference sections land in parts of their own.
    private static readonly Dictionary<string, string?> SmallParts = new() { ["Ai:Anthropic:GenerationPartSourceTokens"] = "1500" };

    private static int Count(string text, string value) =>
        Regex.Matches(text, Regex.Escape(value), RegexOptions.IgnoreCase).Count;

    [Fact]
    public async Task A_genuinely_missing_part_stops_the_run_right_after_it_with_an_exact_request_count()
    {
        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            return Ok(prompt.Contains("FR-250:", StringComparison.Ordinal) ? EmptyAnswer : Faithful(prompt));
        }), SmallParts);
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        var prompts = claude.MessageUserPrompts;
        var staging = AiRouteInspector.Inspect(claude.Client).Primary!.OutputStaging!;
        var planned = ChunkPlanner.PlanStaged(SystemPrompt(claude.MessageRequests[0]), Srs(400), string.Empty,
            GenerationSupport.SharedContext.None, staging);
        var failed = 1 + prompts.FindIndex(p => p.Contains("FR-250:", StringComparison.Ordinal));
        Assert.True(failed > 1);
        Assert.Equal(failed, prompts.Count);                       // no later part was sent: the gap was already certain
        Assert.True(planned.Chunks.Count > prompts.Count);
        var gap = Assert.Single(ex.Parts);
        Assert.Equal((failed, planned.Chunks.Count), (gap.Part, gap.PartCount));
        Assert.Equal(failed, ex.Data[GenerationSupport.FailedPartKey]);
        Assert.Equal(planned.Chunks.Count, ex.Data[GenerationSupport.PartCountKey]);
        Assert.StartsWith($"Part {failed} of {planned.Chunks.Count} of the requirement source", ex.Message);
        Assert.Equal(RequirementLine.Matches(prompts[^1]).Select(m => m.Groups[1].Value), gap.MissingIds);
        Assert.Equal(prompts.Count, ex.RequestsSent);
        Assert.Contains($"This run sent {prompts.Count} AI request(s) to Anthropic", ex.Message);
        Assert.Equal(1, Count(ex.Message, "nothing was saved"));
        Assert.DoesNotContain("Try again.", ex.Message);           // no promise that a retry fixes it
        Assert.Contains("generating again will fail the same way", ex.Message);
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task The_request_count_includes_the_repair_attempt()
    {
        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            if (!prompt.Contains("FR-250:", StringComparison.Ordinal)) return Ok(Faithful(prompt));
            return Ok(prompt.Contains("rejected by automatic validation", StringComparison.Ordinal) ? EmptyAnswer : "not json");
        }), SmallParts);
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.Equal(2, claude.MessageUserPrompts.Count(p => p.Contains("FR-250:", StringComparison.Ordinal)));
        Assert.Equal(claude.MessageRequests.Count, ex.RequestsSent);  // the old message said "at least" one fewer
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task An_id_cited_by_another_part_without_its_own_requirement_still_fails()
    {
        // The first part comes back empty. A later part mentions FR-001, and its requirement cites FR-001 -
        // but quotes only its own line, which is not FR-001's passage: a citation, not coverage.
        const string note = "Note: the nightly export also depends on FR-001.";
        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            if (prompt.Contains("FR-001:", StringComparison.Ordinal)) return Ok(EmptyAnswer);
            return Ok(Faithful(prompt, (p, frs, _) =>
            {
                if (!p.Contains(note, StringComparison.Ordinal)) return;
                var cited = Fr("FR-390", note, "Nightly export dependency");
                cited["sourceIds"] = new JsonArray(JsonValue.Create("FR-390"), JsonValue.Create("FR-001"));
                frs.Add(cited);
            }));
        }), SmallParts);
        var (db, source) = Seed(Srs(400, after: new Dictionary<int, string> { [390] = note }));

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        var prompts = claude.MessageUserPrompts;
        Assert.Contains(prompts, p => p.Contains(note, StringComparison.Ordinal)); // the citing part was answered
        var gap = Assert.Single(ex.Parts);
        Assert.Equal((1, prompts.Count), (gap.Part, gap.PartCount));                // decided after the last part
        Assert.Contains("FR-001", gap.MissingIds);
        Assert.Equal(prompts.Count, ex.RequestsSent);
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task Two_empty_parts_are_both_named_with_their_final_numbers()
    {
        var answered = 0;
        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            return Ok(Interlocked.Increment(ref answered) <= 2 ? EmptyAnswer : Faithful(prompt));
        }), SmallParts);
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        var count = claude.MessageRequests.Count;
        Assert.Equal([(1, count), (2, count)], ex.Parts.Select(g => (g.Part, g.PartCount)).ToList());
        Assert.Null(ex.Data[GenerationSupport.FailedPartKey]);   // several parts: named in the message and in Parts
        Assert.StartsWith("2 parts of the requirement source", ex.Message);
        Assert.Equal(1, Count(ex.Message, "nothing was saved"));
        Assert.Equal(0, db.SaveCount);
    }

    private static string WithReferenceSections(string body) =>
        "# Table of contents\n\n" +
        string.Concat(Enumerable.Range(1, 400).Select(i => $"FR-{i:000} Booking event {i} ........ {3 + i / 10}\n")) +
        "FR-401 Removed booking export ........ 40\n\n" +
        body +
        "\n## Appendix A Traceability matrix\n\n| Requirement | Use case | Test case |\n|---|---|---|\n" +
        string.Concat(Enumerable.Range(1, 400).Select(i => $"| FR-{i:000} | UC-{i % 12 + 1:00} | TC-{i:000} |\n")) +
        string.Concat(Enumerable.Range(401, 5).Select(i => $"| FR-{i:000} | UC-01 | TC-{i:000} |\n")) +
        "\n## Appendix B Standards\n\n" +
        string.Concat(Enumerable.Range(0, 40).Select(i => $"| ISO-{27001 + i} | Referenced information security standard {i} |\n")) +
        "\n## Appendix C Glossary\n\n" +
        string.Concat(Enumerable.Range(1, 40).Select(i => $"| GL-{i:000} | Term {i}: a word used in this document |\n")) +
        "\n## Appendix D Revision history\n\n" +
        string.Concat(Enumerable.Range(0, 30).Select(i => $"| REV-1.{i} | 2026-01-{i % 28 + 1:00} | Editorial changes |\n"));

    [Fact]
    public async Task A_table_of_contents_traceability_matrix_and_reference_appendices_do_not_fail_the_run()
    {
        var srs = WithReferenceSections(Srs(400));
        await using var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r)))), SmallParts);
        var (db, source) = Seed(srs);

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        var prompts = claude.MessageUserPrompts;
        Assert.True(prompts.Count(p => Ids(p) == 0) >= 3, "the reference sections were not in parts of their own");
        Assert.Contains(prompts, p => Ids(p) == 0 && p.Contains("........ ", StringComparison.Ordinal));
        Assert.Contains(prompts, p => Ids(p) == 0 && p.Contains("| FR-401 | UC-01 |", StringComparison.Ordinal));
        Assert.Equal(400, result.FunctionalRequirements.Count);
        Assert.True(db.SaveCount > 0);
        // Not failed, but not hidden either: the reference IDs stay visible as uncovered.
        Assert.Equal(SourceIdCoverage.Incomplete, result.SourceIdCoverage!.Status);
        Assert.Contains("ISO-27001", result.SourceIdCoverage.Uncovered);
        Assert.Contains("GL-001", result.SourceIdCoverage.Uncovered);
        Assert.Contains("REV-1.0", result.SourceIdCoverage.Uncovered);
        Assert.Contains("FR-401", result.SourceIdCoverage.Uncovered);
    }

    [Fact]
    public async Task A_real_requirement_inside_a_glossary_part_is_the_only_id_reported_missing()
    {
        var glossary = new StringBuilder("\n## Appendix C Glossary\n\n");
        for (var i = 1; i <= 300; i++)
        {
            glossary.Append($"| GL-{i:000} | Term {i}: a word used in this document |\n");
            if (i == 150) glossary.Append("FR-401: The system shall archive closed bookings after twelve months.\n");
        }

        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            return Ok(prompt.Contains("FR-401:", StringComparison.Ordinal) ? EmptyAnswer : Faithful(prompt));
        }), SmallParts);
        var (db, source) = Seed(Srs(400) + glossary);

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.Equal(1, Ids(Assert.Single(claude.MessageUserPrompts, p => p.Contains("FR-401:", StringComparison.Ordinal))));
        Assert.Equal(["FR-401"], Assert.Single(ex.Parts).MissingIds);
        Assert.DoesNotContain("GL-", ex.Message);
        Assert.Equal(0, db.SaveCount);
    }

    private static string GermanSrs(int count)
    {
        var text = new StringBuilder("# Lastenheft Terminbuchung\n\n");
        for (var i = 1; i <= count; i++)
        {
            if ((i - 1) % 10 == 0) text.Append($"\n## 3.{(i - 1) / 10 + 1} Buchungsbereich {(i - 1) / 10 + 1}\n\n");
            text.Append($"FR-{i:000}: Das System muss Buchungsereignis {i} mit Zeitstempel, Raum und Ärztin für die Prüfung speichern.\n");
        }

        return text.ToString();
    }

    [Fact]
    public async Task A_german_srs_is_generated_completely_and_a_german_omission_still_fails()
    {
        await using (var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r)))), SmallParts))
        {
            var (db, source) = Seed(GermanSrs(300));
            var result = await Generation(db, claude.Client).GenerateAsync(source.Id);
            Assert.Equal(300, result.FunctionalRequirements.Count);
            Assert.Equal(SourceIdCoverage.Complete, result.SourceIdCoverage!.Status);
            Assert.All(Payloads<FunctionalRequirementPayload>(db, ArtifactType.FunctionalRequirement),
                p => Assert.Equal("verified", p.EvidenceStatus));
        }

        await using (var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            return Ok(prompt.Contains("FR-150:", StringComparison.Ordinal) ? EmptyAnswer : Faithful(prompt));
        }), SmallParts))
        {
            var (db, source) = Seed(GermanSrs(300));
            var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));
            Assert.Contains("FR-150", Assert.Single(ex.Parts).MissingIds);
            Assert.Equal(0, db.SaveCount);
        }
    }

    // A whole source of 400 IDs cut off above 60 IDs per answer: 400 -> 200 + 200 -> 100 + 100 ... -> 50s,
    // so several parts are re-split, some of them twice (nested).
    private static AnthropicAiChatClientTests.FakeHandler TruncatingAbove60(Func<string, bool> empty, List<string> answered) => Answering(r =>
    {
        var prompt = UserPrompt(r);
        if (Ids(prompt) > 60) return Ok("{\"functionalRequirements\": [{\"title\": \"Requirement FR-0", "max_tokens");
        answered.Add(prompt);
        return Ok(empty(prompt) ? EmptyAnswer : Faithful(prompt));
    });

    private static readonly Dictionary<string, string?> LargeParts = new() { ["Ai:Anthropic:GenerationPartSourceTokens"] = "12000" };

    [Fact]
    public async Task After_nested_re_splits_an_early_stop_names_the_part_by_its_position_in_the_final_order()
    {
        var answered = new List<string>();
        await using var claude = new ClaudeStack(TruncatingAbove60(p => p.Contains("FR-317:", StringComparison.Ordinal), answered), LargeParts);
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.True(claude.MessageRequests.Count - answered.Count >= 3, "expected several truncated, re-split parts");
        var failed = 1 + answered.FindIndex(p => p.Contains("FR-317:", StringComparison.Ordinal));
        Assert.Equal(answered.Count, failed);                            // stopped right there
        var gap = Assert.Single(ex.Parts);
        Assert.Equal(failed, gap.Part);
        Assert.True(gap.PartCount >= failed);
        Assert.Equal(failed, ex.Data[GenerationSupport.FailedPartKey]);
        Assert.Equal(gap.PartCount, ex.Data[GenerationSupport.PartCountKey]);
        Assert.Contains("FR-317", gap.MissingIds);
        Assert.Equal(claude.MessageRequests.Count, ex.RequestsSent);    // truncated requests count too
        // Every requirement line before the stop was answered exactly once, in order.
        var ids = answered.SelectMany(p => RequirementLine.Matches(p).Select(m => int.Parse(m.Groups[1].Value[3..]))).ToList();
        Assert.Equal(Enumerable.Range(1, ids.Count), ids);
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task After_re_splits_later_in_the_run_a_gap_is_numbered_against_the_final_part_count()
    {
        // The first part answered comes back empty; the parts after it are still being re-split, so the
        // part count when it was answered is smaller than the final one the error must report.
        var answered = new List<string>();
        await using var claude = new ClaudeStack(TruncatingAbove60(p => p.Contains("FR-001:", StringComparison.Ordinal), answered), LargeParts);
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        var gap = Assert.Single(ex.Parts);
        Assert.Equal((1, answered.Count), (gap.Part, gap.PartCount));
        Assert.Equal(answered.Count, ex.Data[GenerationSupport.PartCountKey]);
        Assert.Equal(RequirementLine.Matches(answered[0]).Select(m => m.Groups[1].Value), gap.MissingIds);
        var ids = answered.SelectMany(p => RequirementLine.Matches(p).Select(m => m.Groups[1].Value)).ToList();
        Assert.Equal(400, ids.Count);
        Assert.Equal(400, ids.Distinct().Count());
        Assert.Equal(claude.MessageRequests.Count, ex.RequestsSent);
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task Identical_requirements_restated_by_several_parts_are_merged_into_one()
    {
        const string common = "The system shall keep an audit log of every booking change.";
        await using var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r), (_, frs, _) =>
            frs.Add(new JsonObject
            {
                ["title"] = "Audit logging", ["actor"] = "System", ["priority"] = "High", ["expectedResult"] = common,
                ["dependencies"] = new JsonArray(), ["businessObjectiveCodes"] = new JsonArray(), ["sourceReferences"] = new JsonArray(),
                ["sourceIds"] = new JsonArray(), ["dependsOnSourceIds"] = new JsonArray(), ["requirementOrigin"] = "derived",
                ["derivationNote"] = "Implied by every booking event.", ["assumedClarifications"] = new JsonArray(), ["evidence"] = new JsonArray()
            })))));
        var (db, source) = Seed(Srs(400));

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        Assert.True(claude.MessageRequests.Count > 1);
        Assert.Equal(401, result.FunctionalRequirements.Count); // 400 + one merged "Audit logging"
        Assert.Single(Payloads<FunctionalRequirementPayload>(db, ArtifactType.FunctionalRequirement), p => p.ExpectedResult == common);
    }

    [Fact]
    public async Task A_source_id_restated_differently_in_another_part_is_kept_twice_and_flagged_for_review()
    {
        // FR-001 is defined in the first part and referred to by a note in the last part.
        var srs = Srs(400, after: new Dictionary<int, string> { [390] = "Note: booking audit also applies as described in FR-001." });
        await using var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r), (prompt, frs, _) =>
        {
            if (AuditNote.Match(prompt) is { Success: true } note)
            {
                frs.Add(Fr("FR-001", note.Groups[1].Value, "Audit for booking events"));
            }
        }))));
        var (db, source) = Seed(srs);

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        Assert.Equal(401, result.FunctionalRequirements.Count);  // nothing dropped or overwritten
        Assert.Equal(["FR-001"], result.Completeness!.CrossPartSharedSourceIds);
        Assert.Equal(2, Payloads<FunctionalRequirementPayload>(db, ArtifactType.FunctionalRequirement).Count(p => p.SourceIds.Contains("FR-001")));
    }

    [Fact]
    public async Task Conflicting_numeric_targets_from_different_parts_are_both_kept_and_counted_as_a_conflict()
    {
        var srs = Srs(400, after: new Dictionary<int, string>
        {
            [5] = "NFR-001: Availability of the booking service shall be 99.9% per month.",
            [395] = "NFR-002: Availability of the booking service shall be 99.99% per month."
        });
        await using var claude = new ClaudeStack(Answering(r => Ok(Faithful(UserPrompt(r)))));
        var (db, source) = Seed(srs);

        var result = await Generation(db, claude.Client).GenerateAsync(source.Id);

        Assert.Equal(2, result.NonFunctionalRequirements.Count);
        Assert.Equal(1, result.Completeness!.CrossPartTargetConflicts);
        var nfrs = Payloads<NonFunctionalRequirementPayload>(db, ArtifactType.NonFunctionalRequirement);
        Assert.Equal(["99.9%", "99.99%"], nfrs.Select(n => n.TargetValue).Order().ToList());
        Assert.All(nfrs, n => Assert.Equal("quote_found", n.TargetSupport));  // each target traced to its own line
    }

    [Fact]
    public async Task Malformed_json_in_one_part_gets_one_repair_and_then_fails_the_whole_run()
    {
        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            return Ok(prompt.Contains("FR-300:", StringComparison.Ordinal) ? "Here are the requirements: FR-300 ..." : Faithful(prompt));
        }));
        var (db, source) = Seed(Srs(400));

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.True(ex.RepairAttempted);
        Assert.Equal(2, claude.MessageUserPrompts.Count(p => p.Contains("FR-300:", StringComparison.Ordinal)));
        Assert.Equal(0, db.SaveCount);
    }
}
