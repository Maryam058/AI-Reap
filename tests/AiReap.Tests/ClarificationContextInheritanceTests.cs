using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// Context inheritance for cross-referenced clarification questions (RuleContextIndex + RuleRelations,
// CqValidation-v3): a rule is compared with the headings, list lead-ins, scope sentences, table
// captions/headers and definitions it inherits - each traced to its source line - and a scope that is
// not written is unknown. Offline: no AI provider, no SQL Server, no network; the end-to-end case runs
// the real RequirementAnalysisService over InMemoryAiReapDb with a scripted model response.
public class ClarificationContextInheritanceTests(ITestOutputHelper output)
{
    private const string NotSupported = "Wrist/hand referrals are not supported by Careway.";
    private const string CallTwice = "For bilateral Wrist referrals, call the API twice.";
    private const string WristQuestion = "Should bilateral Wrist referrals be rejected as unsupported, or sent to the API twice?";

    private static string[] LinesOf(string srs) => SourceReferenceIndex.Lines(srs);
    private static int LineOf(string srs, string text) => Array.FindIndex(LinesOf(srs), l => l.Contains(text, StringComparison.Ordinal));

    private static ClarificationCandidate Candidate(string kind, string question, params string[] evidence) =>
        new(question, question, "Checked every related section; nothing says how the rules relate.", kind,
            "Decides how the integration is implemented and tested.", evidence);

    private CandidateVerdict Check(string srs, ClarificationCandidate candidate)
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(new ClarificationCandidateValidator.Context(Guid.NewGuid(), srs, []), candidate);
        output.WriteLine($"{(verdict.Accepted ? "ACCEPTED" : "REJECTED")} [{verdict.Kind}] {verdict.Candidate.Question}");
        foreach (var r in verdict.Rejections) output.WriteLine($"    {r.Code}: {r.Message}");
        if (verdict.Relation is { } rel)
        {
            foreach (var rule in new[] { rel.First, rel.Second })
            {
                output.WriteLine($"    rule (line {rule.Line + 1}): \"{rule.Text}\" | condition: \"{rule.Condition.Trim()}\" | outcome: \"{rule.Outcome.Trim()}\"");
                foreach (var c in rule.Context) output.WriteLine($"        inherits {c.Kind} (line {c.Line + 1}): {c.Text}");
                foreach (var s in rule.Scope.Values) output.WriteLine($"        scope {s.Dimension} = {s.Value} from {s.From}");
            }
        }

        return verdict;
    }

    private static RuleStatement Rule(string srs, string text)
    {
        var document = RuleContextIndex.Build(srs);
        var line = LineOf(srs, text);
        return RuleRelations.Read(document.Lines[line], line, text, document);
    }

    // ---- 1. section headings ------------------------------------------------------------------------

    [Fact]
    public void T1_both_rules_inherit_the_same_heading_and_shared_context_alone_is_not_a_contradiction_or_a_resolution()
    {
        var srs = $"""
            # Careway Integration

            ## New Referrals
            {NotSupported}
            {CallTwice}
            """;

        var a = Rule(srs, NotSupported);
        var b = Rule(srs, CallTwice);
        Assert.Contains(a.Context, c => c.Kind == "heading" && c.Text == "## New Referrals" && c.Line == LineOf(srs, "## New Referrals"));
        Assert.Contains(b.Context, c => c.Kind == "heading" && c.Text == "## New Referrals");
        Assert.Equal("create", a.Scope["workflow"].Value);
        Assert.Equal("create", b.Scope["workflow"].Value);

        // Same context, outcomes still compared: one refuses, the other calls the API - kept as a question.
        var verdict = Check(srs, Candidate("contradiction", WristQuestion, NotSupported, CallTwice));
        Assert.True(verdict.Accepted);
        Assert.NotNull(verdict.Relation!.Conflict);
    }

    // ---- 2. subheadings ----------------------------------------------------------------------------

    [Fact]
    public void T2_subheadings_are_inherited_by_hierarchy_and_sibling_subsections_are_different_workflows()
    {
        var srs = $"""
            Careway Integration SRS

            4. Referral processing
            4.1 New referrals
            {NotSupported}
            4.2 Referral amendments
            {CallTwice}
            """;

        var a = Rule(srs, NotSupported);
        var b = Rule(srs, CallTwice);
        Assert.Equal(["4. Referral processing", "4.1 New referrals"], a.Context.Where(c => c.Kind == "heading").Select(c => c.Text));
        Assert.Equal(["4. Referral processing", "4.2 Referral amendments"], b.Context.Where(c => c.Kind == "heading").Select(c => c.Text));
        Assert.Equal([1, 2], a.Context.Where(c => c.Kind == "heading").Select(c => c.Level));

        var verdict = Check(srs, Candidate("contradiction", WristQuestion, NotSupported, CallTwice));
        Assert.False(verdict.Accepted);
        var rejection = Assert.Single(verdict.Rejections, r => r.Code == "different-conditions");
        Assert.Contains("different workflows", rejection.Message);
        Assert.Contains("4.1 New referrals", rejection.Message);
        Assert.Contains("4.2 Referral amendments", rejection.Message);
    }

    // ---- 3. preceding paragraphs (and a definition given earlier) --------------------------------------

    [Fact]
    public void T3_a_scope_sentence_applies_to_the_rest_of_its_section_only()
    {
        var srs = $"""
            # Careway Integration

            ## 5. Careway clients
            The following rules apply only when 'Is Careway' is enabled for the client.

            {NotSupported}

            ## 6. Other clients
            The following rules apply when 'Is Careway' is disabled for the client.

            {CallTwice}

            ## 7. Reporting
            Bilateral Wrist referrals are counted once in the monthly report.
            """;

        var a = Rule(srs, NotSupported);
        var b = Rule(srs, CallTwice);
        Assert.Contains(a.Context, c => c.Kind == "scope" && c.Text.Contains("is enabled") && c.Line == LineOf(srs, "is enabled"));
        Assert.DoesNotContain(a.Context, c => c.Text.Contains("is disabled"));
        Assert.Contains(b.Context, c => c.Kind == "scope" && c.Text.Contains("is disabled"));
        Assert.DoesNotContain(Rule(srs, "counted once").Context, c => c.Kind == "scope"); // section 7 inherits neither

        var verdict = Check(srs, Candidate("contradiction", WristQuestion, NotSupported, CallTwice));
        var rejection = Assert.Single(verdict.Rejections, r => r.Code == "different-conditions");
        Assert.Contains("Is Careway", rejection.Message);
        Assert.Contains("inherited from the scope", rejection.Message);
    }

    [Fact]
    public void T3b_a_condition_defined_earlier_is_attached_to_the_rule_that_uses_the_term()
    {
        var srs = """
            # Triage

            ## 2. Definitions
            A referral is 'complex' when it involves more than one body part.

            ## 5. Routing
            Complex referrals require manual triage by a clinician.
            """;

        var rule = Rule(srs, "Complex referrals");
        var definition = Assert.Single(rule.Context, c => c.Kind == "definition");
        Assert.Equal("A referral is 'complex' when it involves more than one body part.", definition.Text);
        Assert.Equal(LineOf(srs, "is 'complex'"), definition.Line);
    }

    // ---- 4. numbered lists -------------------------------------------------------------------------

    [Fact]
    public void T4_list_items_inherit_their_own_lead_in_and_nested_items_their_parent_item()
    {
        var srs = """
            # Careway Integration

            ## 3. Referral handling
            When a new referral is received:
            1. Check the injury type.
            2. Wrist/hand referrals are rejected with an error to Axis.

            When a referral is amended:
            1. Check the body side.
            2. For bilateral Wrist referrals, call the API twice.
            """;

        var a = Rule(srs, "Wrist/hand referrals are rejected");
        var b = Rule(srs, "For bilateral Wrist referrals");
        Assert.Equal(["When a new referral is received:"], a.Context.Where(c => c.Kind == "lead-in").Select(c => c.Text));
        Assert.Equal(["When a referral is amended:"], b.Context.Where(c => c.Kind == "lead-in").Select(c => c.Text));

        var verdict = Check(srs, Candidate("contradiction", WristQuestion,
            "Wrist/hand referrals are rejected with an error to Axis.", "For bilateral Wrist referrals, call the API twice."));
        Assert.Contains(verdict.Rejections, r => r.Code == "different-conditions" && r.Message.Contains("different workflows"));

        var nested = """
            Referral rules:
            1. When the referral is new:
               a. Wrist/hand referrals are rejected with an error to Axis.
            2. When the referral is an amendment:
               a. For bilateral Wrist referrals, call the API twice.
            """;
        Assert.Equal(["Referral rules:", "1. When the referral is new:"],
            Rule(nested, "Wrist/hand referrals").Context.Where(c => c.Kind == "lead-in").Select(c => c.Text));
        Assert.Equal(["Referral rules:", "2. When the referral is an amendment:"],
            Rule(nested, "For bilateral").Context.Where(c => c.Kind == "lead-in").Select(c => c.Text));
    }

    // ---- 5. tables ----------------------------------------------------------------------------------

    private const string TableSrs = """
        # Careway Integration

        ## 3. Referral validation
        Wrist/hand referrals are not supported by Careway.

        ## 4. Bilateral processing
        Table 3: Body part handling for new referrals
        | Body Part | Body Side | Action |
        | --- | --- | --- |
        | Wrist | Bilateral | Call the API twice with Left and Right |
        | Wrist | Left | Call the API once with Left |
        | Knee | Bilateral | Call the API twice with Left and Right |
        """;

    [Fact]
    public void T5_a_table_row_keeps_its_column_and_caption_context()
    {
        var document = RuleContextIndex.Build(TableSrs);
        var line = LineOf(TableSrs, "| Wrist | Bilateral |");
        var row = document.RowAt(line)!;
        Assert.Equal("Body Part = Wrist and Body Side = Bilateral", row.Condition);
        Assert.Equal("Call the API twice with Left and Right", row.Outcome);
        Assert.Equal(["table-title", "table-header"], document.ContextOf(line).Where(c => c.Kind.StartsWith("table")).Select(c => c.Kind));
        Assert.Null(document.RowAt(LineOf(TableSrs, "| --- |")));

        // Two rows for Wrist differ by Body Side: different conditions, no question.
        var rows = Check(TableSrs, Candidate("contradiction", "Should a Wrist referral call the API twice with Left and Right or once with Left?",
            "Wrist | Bilateral | Call the API twice with Left and Right", "Wrist | Left | Call the API once with Left"));
        Assert.Contains(rows.Rejections, r => r.Code == "different-conditions" && r.Message.Contains("Body Side"));

        // The bilateral Wrist row against the unsupported rule: overlapping, unresolved - kept.
        var overlap = Check(TableSrs, Candidate("contradiction", WristQuestion,
            NotSupported, "Wrist | Bilateral | Call the API twice with Left and Right"));
        Assert.True(overlap.Accepted);
        Assert.Contains("Body Side = Bilateral", overlap.Relation!.Second.Condition);
    }

    // ---- 6. different workflows in separate sections ----------------------------------------------------

    [Fact]
    public void T6_rules_for_different_workflows_in_separate_sections_are_not_contradictions()
    {
        var submission = """
            # Referral Service

            ## 3. Initial submission
            A referral without a GP letter is rejected.

            ## 4. Resubmission
            A referral without a GP letter is accepted and flagged for review.
            """;
        var resubmit = Check(submission, Candidate("contradiction", "Is a referral without a GP letter rejected or accepted and flagged for review?",
            "A referral without a GP letter is rejected.", "A referral without a GP letter is accepted and flagged for review."));
        Assert.Contains(resubmit.Rejections, r => r.Code == "different-conditions" && r.Message.Contains("initial or resubmission"));

        var lifecycle = """
            # Case Management

            ## 3. Case creation
            The claimant's email address is mandatory.

            ## 4. Case update
            The claimant's email address is optional.
            """;
        var fields = Check(lifecycle, Candidate("contradiction", "Is the claimant's email address mandatory or optional?",
            "The claimant's email address is mandatory.", "The claimant's email address is optional."));
        Assert.NotNull(fields.Relation!.Conflict);   // mandatory against optional is a real difference ...
        Assert.Contains(fields.Rejections, r => r.Code == "different-conditions" && r.Message.Contains("different workflows")); // ... in different workflows
        Assert.DoesNotContain(fields.Rejections, r => r.Code == "compatible-rules");
    }

    // ---- 7. explicit exceptions --------------------------------------------------------------------

    [Fact]
    public void T7_only_an_explicitly_stated_exception_settles_a_general_rule()
    {
        static string Srs(string second) => $"""
            # Careway Integration

            ## 3. Referral validation
            {NotSupported}

            ## 4. Pilot client
            {second}
            """;

        const string exception = "As an exception, bilateral Wrist referrals from the pilot client are sent to the API twice.";
        var stated = Check(Srs(exception), Candidate("contradiction", WristQuestion, NotSupported, exception));
        Assert.Contains(stated.Rejections, r => r.Code == "different-conditions" && r.Message.Contains("explicit exception"));

        // A condition alone does not make the rule an exception to the general one.
        const string conditional = "If the client is the pilot client, bilateral Wrist referrals are sent to the API twice.";
        var notStated = Check(Srs(conditional), Candidate("contradiction", WristQuestion, NotSupported, conditional));
        Assert.True(notStated.Accepted);
    }

    // ---- 8. unrelated sections with similar terminology ---------------------------------------------------

    [Fact]
    public void T8_the_same_words_in_unrelated_sections_are_not_a_contradiction()
    {
        var srs = """
            # Notifications

            ## 5. Referral notifications
            A notification is sent to the case handler within 2 working days.

            ## 6. Invoice notifications
            A notification is sent to the case handler within 5 working days.
            """;

        var verdict = Check(srs, Candidate("contradiction", "Is the notification sent to the case handler within 2 or 5 working days?",
            "A notification is sent to the case handler within 2 working days.", "A notification is sent to the case handler within 5 working days."));

        Assert.False(verdict.Accepted);
        var rejection = Assert.Single(verdict.Rejections, r => r.Code == "different-subjects");
        Assert.Contains("Referral notifications", rejection.Message);
        Assert.Contains("Invoice notifications", rejection.Message);
    }

    // ---- 9. missing or ambiguous inherited context ------------------------------------------------------

    [Fact]
    public void T9_missing_or_ambiguous_context_is_unknown_and_never_makes_rules_exclusive()
    {
        // Only one rule states a workflow: the other's is unknown, not "different".
        var oneSided = $"""
            # Careway Integration

            ## 3. New referrals
            {NotSupported}

            ## 4. Processing rules
            {CallTwice}
            """;
        var unknown = Check(oneSided, Candidate("contradiction", WristQuestion, NotSupported, CallTwice));
        Assert.True(unknown.Accepted);
        Assert.False(unknown.Relation!.Second.Scope.ContainsKey("workflow"));

        // A heading naming both workflows states neither.
        var both = $"""
            # Careway Integration

            ## 3. New referrals and amendments
            {NotSupported}

            ## 4. Referral amendments
            {CallTwice}
            """;
        var mixed = Check(both, Candidate("contradiction", WristQuestion, NotSupported, CallTwice));
        Assert.True(mixed.Accepted);
        Assert.False(mixed.Relation!.First.Scope.ContainsKey("workflow"));

        // A lead-in cut off from the paragraph by a blank line is not attached to it.
        var detached = $"""
            # Careway Integration

            ## 3. Referral rules
            When a new referral is received:
            1. Wrist/hand referrals are rejected with an error to Axis.

            When a referral is amended:

            {CallTwice}
            """;
        Assert.DoesNotContain(Rule(detached, CallTwice).Context, c => c.Kind == "lead-in");
        var cut = Check(detached, Candidate("contradiction", WristQuestion, "Wrist/hand referrals are rejected with an error to Axis.", CallTwice));
        Assert.True(cut.Accepted);
    }

    // ---- 10. the original Careway scenario ------------------------------------------------------------

    private const string UnsupportedRule =
        "If Careway receives a referral with Thoracic Spine, Lower Limb, Head, Upper Limb or Wrist/hand, an error will be returned to Axis stating that the injury type is not supported by Careway.";

    private const string BilateralRule =
        "If Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist, call the API twice with the same information for Left and Right.";

    private const string IsCarewayRule =
        "If 'Is Careway' is disabled for the client, referrals are not sent to Careway and Axis processes the referral as normal.";

    private static readonly string CarewaySrs = $"""
        # Careway Integration - Software Requirements Specification

        ## 1. Overview
        Axis sends injury referrals to Careway through the Careway referral API.

        ## 3. Referral validation
        {UnsupportedRule}

        ## 4. Bilateral referrals
        {BilateralRule}

        ## 5. Client configuration
        {IsCarewayRule}
        """;

    [Fact]
    public async Task T10_the_original_Careway_question_is_kept_with_its_inherited_context_traced_to_the_source()
    {
        var unsupported = Rule(CarewaySrs, "Thoracic Spine");
        var bilateral = Rule(CarewaySrs, "Body Side = Bilateral");
        Assert.Equal(["# Careway Integration - Software Requirements Specification", "## 3. Referral validation"],
            unsupported.Context.Where(c => c.Kind == "heading").Select(c => c.Text));
        Assert.Contains(bilateral.Context, c => c.Text == "## 4. Bilateral referrals");
        Assert.Empty(unsupported.InheritedStates);              // the 'Is Careway' rule is a rule of its own section, not inherited
        Assert.False(unsupported.Scope.ContainsKey("laterality")); // unknown for the unsupported rule ...
        Assert.Equal("bilateral", bilateral.Scope["laterality"].Value); // ... so it never makes the two exclusive

        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "CQ context project", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = CarewaySrs,
            CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        var ai = new ScriptedAiChatClient(new JsonObject
        {
            ["actors"] = new JsonArray("Axis", "Careway"),
            ["capabilities"] = new JsonArray("Validate referral"),
            ["dataElements"] = new JsonArray("Referral"),
            ["notes"] = new JsonArray(),
            ["missingInformation"] = new JsonArray(new JsonObject
            {
                ["kind"] = "contradiction", ["topic"] = "Bilateral Wrist referrals",
                ["question"] = "Should a bilateral Wrist referral be rejected as an unsupported injury type, or sent to the API twice for Left and Right?",
                ["reason"] = "Sections 3 and 4 both cover Wrist; neither says which applies.",
                ["impact"] = "Decides how the integration is implemented and tested.",
                ["evidence"] = new JsonArray(UnsupportedRule, BilateralRule)
            })
        }.ToJsonString());

        await new RequirementAnalysisService(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(),
            new BatchModeOff(), NullLogger<RequirementAnalysisService>.Instance).AnalyzeAsync(source.Id);

        var saved = Assert.Single(InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion));
        var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(saved.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var comparison = payload.RuleComparison!;
        Assert.Equal(["Wrist"], comparison.SharedSubject);
        Assert.Equal([LineOf(CarewaySrs, "Thoracic Spine"), LineOf(CarewaySrs, "Body Side = Bilateral")], comparison.Rules.Select(r => r.Line));
        Assert.Contains("not supported by Careway", comparison.Rules[0].Outcome);
        Assert.Contains("Body Side = Bilateral", comparison.Rules[1].Condition);

        // Every inherited item is real source text at the line it claims - nothing fabricated.
        var lines = LinesOf(CarewaySrs);
        Assert.All(comparison.Rules.SelectMany(r => r.Inherited), c => Assert.Equal(lines[c.Line].Trim(), c.Text));
        Assert.Contains(comparison.Rules[1].Inherited, c => c.Kind == "heading" && c.Text == "## 4. Bilateral referrals");
    }
}
