using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Files;
using AiReap.Tests.SrsEval;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// Phase 3 final review: condition-specific duplicates (a general question does not cover a narrower one
// with a real condition), settings that settle a rule only when the source establishes the link (never
// similar wording alone), assumption-based questions whose gap is held for review rather than lost, and
// the document context that is - and is not - inherited (Word heading styles, "Note:"/"Exception:"
// paragraphs, exceptions in other sections). Offline: no AI provider, no SQL Server, no network.
public class ClarificationFinalReviewTests(ITestOutputHelper output)
{
    private static int LineOf(string srs, string text) =>
        Array.FindIndex(SourceReferenceIndex.Lines(srs), l => l.Contains(text, StringComparison.Ordinal));

    private static ClarificationCandidate Candidate(string kind, string question, params string[] evidence) =>
        new(question, question, "Checked every related section; nothing settles it.", kind,
            "Decides how the requirement is implemented and tested.", evidence);

    private static ExistingClarification Existing(string code, string question, string srs, string evidence, string status = "Open") =>
        new(code, question, question, status, new HashSet<int> { LineOf(srs, evidence) }, "missing");

    private CandidateVerdict Check(string srs, ClarificationCandidate candidate, params ExistingClarification[] existing)
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(new ClarificationCandidateValidator.Context(Guid.NewGuid(), srs, existing), candidate);
        output.WriteLine($"{(verdict.Accepted ? "ACCEPTED" : "REJECTED")} [{verdict.Kind}] {verdict.Candidate.Question}");
        foreach (var r in verdict.Rejections) output.WriteLine($"    {r.Code}: {r.Message}");
        return verdict;
    }

    private static string[] Codes(CandidateVerdict verdict) => verdict.Rejections.Select(r => r.Code).ToArray();

    // ---- 1. condition-specific duplicates ----------------------------------------------------------------

    private const string Claims = "Employees submit expense claims through the finance portal.";

    private static readonly string Expenses = $"""
        # Expenses
        ## 2. Claims
        {Claims}
        Claims over £500 are reviewed by the finance team before payment.
        Contractors submit expense claims through their agency.
        """;

    private const string General = "Who approves expense claims?";
    private const string Over500 = "Who approves expense claims over £500?";
    private const string ForContractors = "Who approves expense claims for contractors?";
    private const string ForEmployees = "Who approves expense claims for employees?";

    // Each pair can reasonably have different answers: the narrower question is not covered by the
    // general one, whichever of the two was asked first.
    [Theory]
    [InlineData(General, Over500)]
    [InlineData(Over500, General)]
    [InlineData(General, ForContractors)]
    [InlineData(ForContractors, General)]
    [InlineData(General, ForEmployees)]
    [InlineData(ForContractors, ForEmployees)]
    [InlineData(Over500, ForContractors)]
    public void R1_a_question_with_a_real_condition_the_other_lacks_is_a_separate_question(string existing, string candidate)
    {
        var verdict = Check(Expenses, Candidate("missing", candidate, Claims), Existing("CQ-020", existing, Expenses, Claims));
        Assert.True(verdict.Accepted);
    }

    // The same question in other words, or with padding that names no case of its own, is a duplicate.
    [Theory]
    [InlineData(General, "Who signs off expense claims?")]
    [InlineData(General, "Who approves expense claims in the system?")]
    [InlineData(General, "Who gives final approval for expense claims?")]
    [InlineData(Over500, "Who approves expense claims above £500?")]
    [InlineData(ForContractors, "Who signs off expense claims for contractors?")]
    public void R1_the_same_issue_under_the_same_conditions_is_a_duplicate(string existing, string candidate)
    {
        var verdict = Check(Expenses, Candidate("missing", candidate, Claims), Existing("CQ-020", existing, Expenses, Claims));
        Assert.Equal("CQ-020", Assert.Single(verdict.Rejections, r => r.Code == "duplicate").RelatedCode);
    }

    [Fact]
    public void R1_a_qualifier_that_is_the_documents_general_vocabulary_names_no_case_of_its_own()
    {
        // Every passage is about employees, so "for employees" narrows nothing here.
        var srs = """
            # Expenses
            ## 2. Claims
            Employees submit expense claims through the finance portal.
            Employees attach receipts to each claim.
            Employees may withdraw a claim before it is paid.
            Employees are reimbursed monthly.
            """;
        const string evidence = "Employees submit expense claims through the finance portal.";
        var verdict = Check(srs, Candidate("missing", ForEmployees, evidence), Existing("CQ-020", General, srs, evidence));
        Assert.Contains("duplicate", Codes(verdict));
    }

    [Fact]
    public void R1_a_workflow_or_setting_stated_by_one_question_only_keeps_it_separate()
    {
        var existing = Existing("CQ-020", General, Expenses, Claims);
        Assert.True(Check(Expenses, Candidate("missing", "Who approves amended expense claims?", Claims), existing).Accepted);
        Assert.True(Check(Expenses, Candidate("missing", "Who approves expense claims when 'Auto-approve' is disabled?", Claims), existing).Accepted);
    }

    // ---- 2. settings and the rules they settle -------------------------------------------------------------

    private const string TwoDays = "Leave requests are approved within 2 working days.";

    private static string Leave(string configuration) => $"""
        # Leave
        ## 2. Approval
        {TwoDays}
        ## 4. Configuration
        {configuration}
        """;

    [Fact]
    public void R2_a_setting_the_source_says_controls_the_rule_settles_it_even_when_disabled()
    {
        const string disabled = "If 'Fast track' is disabled, leave requests are approved within 5 working days.";
        var srs = Leave($"""
            The 'Fast track' setting controls how quickly leave requests are approved.
            {disabled}
            """);
        var verdict = Check(srs, Candidate("contradiction", "Are leave requests approved within 2 or 5 working days?", TwoDays, disabled));
        var rejection = Assert.Single(verdict.Rejections, r => r.Code == "different-conditions");
        Assert.Contains($"line {LineOf(srs, "setting controls") + 1}", rejection.Message);
    }

    [Fact]
    public void R2_a_setting_with_a_similar_name_but_another_purpose_settles_nothing()
    {
        // "Approval emails" shares "approval" with the rule; what it controls is reminder emails.
        const string emails = "If 'Approval emails' is disabled, leave requests are approved within 5 working days.";
        var srs = Leave($"""
            The 'Approval emails' setting controls reminder emails sent to approvers.
            {emails}
            """);
        var verdict = Check(srs, Candidate("contradiction", "Are leave requests approved within 2 or 5 working days?", TwoDays, emails));
        Assert.True(verdict.Accepted);
        Assert.Null(verdict.Relation!.ExclusiveConditions);

        // A "controls" sentence that touches one word of the rule is not a link either.
        const string reminders = "If 'Reminders' is disabled, leave requests are approved within 5 working days.";
        var managers = """
            # Leave
            ## 2. Approval
            All leave requests are approved by the line manager within 2 working days.
            ## 4. Configuration
            The 'Reminders' setting controls reminder emails sent to managers.
            If 'Reminders' is disabled, leave requests are approved within 5 working days.
            """;
        Assert.True(Check(managers, Candidate("contradiction", "Are leave requests approved within 2 or 5 working days?",
            "All leave requests are approved by the line manager within 2 working days.", reminders)).Accepted);
    }

    [Fact]
    public void R2_a_disabled_setting_nothing_links_to_the_rule_settles_nothing()
    {
        const string banner = "If 'Holiday banner' is disabled, leave requests are approved within 5 working days.";
        var verdict = Check(Leave(banner), Candidate("contradiction", "Are leave requests approved within 2 or 5 working days?", TwoDays, banner));
        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void R2_a_setting_whose_relationship_to_the_rule_is_unclear_settles_nothing()
    {
        const string fastTrack = "If 'Fast track' is enabled, leave requests are approved within 5 working days.";
        var srs = Leave($"""
            The 'Fast track' setting is configured for each client by the administrator.
            {fastTrack}
            """);
        Assert.True(Check(srs, Candidate("contradiction", "Are leave requests approved within 2 or 5 working days?", TwoDays, fastTrack)).Accepted);
    }

    [Fact]
    public void R2_a_setting_named_after_the_rule_settles_it_only_when_its_rule_reverses_that_same_action()
    {
        const string sends = "Axis sends injury referrals to Careway through the Careway referral API.";
        const string notSent = "If 'Is Careway' is disabled for the client, referrals are not sent to Careway and Axis processes the referral as normal.";
        const string unsupported = "Wrist/hand referrals are not supported by Careway.";
        const string twice = "If 'Is Careway' is enabled, Wrist referrals are sent to the API twice.";
        var srs = $"""
            # Careway Integration
            ## 1. Overview
            {sends}
            ## 3. Validation
            {unsupported}
            ## 5. Client configuration
            {notSent}
            {twice}
            """;

        // The setting switches off exactly what the general rule does: settled.
        var routing = Check(srs, Candidate("contradiction", "Are referrals sent to Careway or processed by Axis as normal?", sends, notSent));
        Assert.Contains("switches", Assert.Single(routing.Rejections, r => r.Code == "different-conditions").Message);

        // The same setting with a different action under it clashes with the rule: the question stays.
        var clash = Check(srs, Candidate("contradiction",
            "Should Wrist referrals be rejected as unsupported by Careway, or sent to the API twice when 'Is Careway' is enabled?", unsupported, twice));
        Assert.True(clash.Accepted);
        Assert.Null(clash.Relation!.ExclusiveConditions);
    }

    // ---- 3. assumptions: the gap is held, never lost --------------------------------------------------------

    private const string UnsupportedRule =
        "If Careway receives a referral with Thoracic Spine, Lower Limb, Head, Upper Limb or Wrist/hand, an error will be returned to Axis stating that the injury type is not supported by Careway.";

    private const string BilateralRule =
        "If Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist, call the API twice with the same information for Left and Right.";

    private const string Assuming = "How many API calls should be made for an unsupported Wrist referral?";
    private const string Neutral = "Does the bilateral referral process apply to Wrist/hand referrals, or are these referrals excluded?";

    private static readonly string CarewaySrs = $"""
        # Careway Integration - Software Requirements Specification

        ## 1. Overview
        Axis sends injury referrals to Careway through the Careway referral API.

        ## 3. Referral validation
        {UnsupportedRule}

        ## 4. Bilateral referrals
        {BilateralRule}
        """;

    [Fact]
    public void R3_the_neutral_question_is_accepted_and_the_assuming_one_is_rejected()
    {
        Assert.Contains("unsupported-assumption", Codes(Check(CarewaySrs, Candidate("missing", Assuming, BilateralRule))));

        var neutral = Check(CarewaySrs, Candidate("ambiguous", Neutral, BilateralRule, UnsupportedRule));
        Assert.True(neutral.Accepted);
    }

    [Fact]
    public void R3_an_assuming_question_is_held_for_review_unless_another_question_covers_its_passages()
    {
        List<HeldClarification> Held(IEnumerable<ExistingClarification> existing, params ClarificationCandidate[] candidates)
        {
            var context = new ClarificationCandidateValidator.Context(Guid.NewGuid(), CarewaySrs, existing);
            return ClarificationCandidateValidator.HeldForReview(context, ClarificationCandidateValidator.Validate(context, candidates));
        }

        // Alone: held, as asked, with both passages and nothing rewritten or answered.
        var held = Assert.Single(Held([], Candidate("missing", Assuming, BilateralRule)));
        Assert.Equal(Assuming, held.Question);
        Assert.Equal(LineOf(CarewaySrs, "Thoracic Spine"), held.DenialLine);
        Assert.Equal(UnsupportedRule, held.DenialText);
        Assert.Contains(held.Evidence, e => e.Line == LineOf(CarewaySrs, "Body Side = Bilateral"));
        Assert.StartsWith("3. Referral validation", held.Location);
        Assert.Contains("reviewer", held.Note);

        // Covered by the neutral question of the same run, or by a saved question on the denying passage.
        Assert.Empty(Held([], Candidate("ambiguous", Neutral, BilateralRule, UnsupportedRule), Candidate("missing", Assuming, BilateralRule)));
        Assert.Empty(Held([Existing("CQ-030", "Are Wrist/hand referrals excluded from the bilateral process?", CarewaySrs, "Thoracic Spine")],
            Candidate("missing", Assuming, BilateralRule)));

        // Rejected for another reason as well (no verified evidence): not a grounded gap, not held.
        Assert.Empty(Held([], Candidate("missing", Assuming, "This sentence is not in the source.")));

        // Two assuming questions on the same passages are held once.
        Assert.Single(Held([], Candidate("missing", Assuming, BilateralRule),
            Candidate("missing", "How many API calls are made for unsupported bilateral Wrist referrals?", BilateralRule)));
    }

    [Fact]
    public async Task R3_end_to_end_the_held_gap_is_recorded_and_returned_and_no_question_is_invented()
    {
        var (lone, loneDb) = await Analyse(Question("missing", "Wrist API calls", Assuming, BilateralRule));
        Assert.Empty(InMemoryAiReapDb.Rows(loneDb.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion));
        Assert.Contains(lone.RejectedCandidates!, r => r.Question == Assuming);
        var held = Assert.Single(lone.HeldForReview!);
        Assert.Equal(Assuming, held.Question);
        Assert.Equal(UnsupportedRule, held.DenialText);

        // The stored analysis returns it again when reused.
        var record = await RequirementAnalysisRecords.LatestAsync(loneDb, InMemoryAiReapDb.Rows(loneDb.RequirementSources).Single(), default);
        Assert.Equal(Assuming, Assert.Single(record!.HeldForReview!).Question);

        var (covered, coveredDb) = await Analyse(
            Question("ambiguous", "Wrist bilateral scope", Neutral, BilateralRule, UnsupportedRule),
            Question("missing", "Wrist API calls", Assuming, BilateralRule));
        Assert.Single(InMemoryAiReapDb.Rows(coveredDb.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion));
        Assert.Null(covered.HeldForReview);
    }

    // ---- 4. document context ------------------------------------------------------------------------------

    [Fact]
    public async Task R4_an_unnumbered_Word_heading_style_is_plain_text_so_its_scope_stays_unknown_and_the_question_is_kept()
    {
        // Recorded limitation: DocumentTextExtractor writes a heading style without numbering as a plain
        // line, so "Amended referrals" is not a heading here. Nothing is inferred from it - the question is
        // kept (the safe direction) rather than settled by a scope the text no longer marks.
        var bytes = Docx(body =>
        {
            body.Append(Styled("Heading1", "Referral processing"));
            body.Append(Styled("Heading2", "New referrals"));
            body.Append(Styled(null, "A GP letter is mandatory."));
            body.Append(Styled("Heading2", "Amended referrals"));
            body.Append(Styled(null, "A GP letter is optional."));
        });
        var srs = (await new DocumentTextExtractor().ExtractDocumentAsync(bytes, "srs.docx")).Text;
        output.WriteLine(srs);
        Assert.Contains("Amended referrals", srs);
        Assert.DoesNotContain("# Amended referrals", srs);

        var document = RuleContextIndex.Build(srs);
        Assert.DoesNotContain(document.ContextOf(LineOf(srs, "A GP letter is optional.")), c => c.Kind == "heading" && c.Text.Contains("Amended"));
        Assert.True(Check(srs, Candidate("contradiction", "Is a GP letter mandatory or optional?", "A GP letter is mandatory.", "A GP letter is optional.")).Accepted);
    }

    [Fact]
    public void R4_an_Exception_paragraph_under_the_rule_makes_it_an_exception()
    {
        const string general = "All referrals are triaged within 5 working days.";
        const string exception = "Exception: urgent referrals are triaged within 1 working day.";
        var srs = $"""
            # Triage
            ## 3. Targets
            {general}
            {exception}
            """;
        Assert.Contains("different-conditions", Codes(Check(srs, Candidate("contradiction",
            "Are urgent referrals triaged within 1 or 5 working days?", general, exception))));
    }

    [Theory]
    [InlineData("The urgent triage target overrides the standard 5 day target.")]
    [InlineData("Note: The 5 day triage target does not apply to urgent referrals.")]
    public void R4_an_exception_or_note_in_another_section_that_names_both_rules_settles_them(string resolver)
    {
        const string standard = "Referrals are triaged within 5 working days.";
        const string urgent = "Urgent referrals are triaged within 1 working day.";
        var srs = $"""
            # Triage
            ## 3. Standard targets
            {standard}
            ## 6. Urgent cases
            {urgent}
            ## 9. Notes
            {resolver}
            """;
        var verdict = Check(srs, Candidate("contradiction", "Are urgent referrals triaged within 1 or 5 working days?", standard, urgent));
        Assert.Equal(LineOf(srs, resolver) + 1, int.Parse(Assert.Single(verdict.Rejections, r => r.Code == "resolved-in-source").Location!.Split("line ")[1].TrimEnd(')')));
    }

    [Fact]
    public void R4_a_note_that_does_not_relate_the_rules_settles_nothing()
    {
        const string standard = "Referrals are triaged within 5 working days.";
        const string urgent = "Urgent referrals are triaged within 1 working day.";
        var srs = $"""
            # Triage
            ## 3. Standard targets
            {standard}
            ## 6. Urgent cases
            {urgent}
            ## 9. Notes
            Note: Triage targets are reviewed every year.
            """;
        Assert.True(Check(srs, Candidate("contradiction", "Are urgent referrals triaged within 1 or 5 working days?", standard, urgent)).Accepted);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private async Task<(AnalyzeRequirementResult Result, InMemoryAiReapDb Db)> Analyse(params JsonObject[] questions)
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "CQ review project", CreatedByUserId = "phase0-offline-user" };
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
            ["missingInformation"] = new JsonArray(questions.Select(q => (JsonNode)q).ToArray())
        }.ToJsonString());

        var result = await new RequirementAnalysisService(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(),
            new BatchModeOff(), NullLogger<RequirementAnalysisService>.Instance).AnalyzeAsync(source.Id);
        foreach (var r in result.RejectedCandidates ?? []) output.WriteLine($"REJECTED {r.Question}: {string.Join("; ", r.Reasons.Select(x => x.Code))}");
        foreach (var h in result.HeldForReview ?? []) output.WriteLine($"HELD {h.Question}: {h.Note}");
        return (result, db);
    }

    private static JsonObject Question(string kind, string topic, string question, params string[] evidence) => new()
    {
        ["kind"] = kind, ["topic"] = topic, ["question"] = question,
        ["reason"] = "Checked every section that mentions it; nothing says how the rules relate.",
        ["impact"] = "Decides how the integration is implemented and tested.",
        ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e).ToArray())
    };

    private static byte[] Docx(Action<Body> fill)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);
            fill(body);
            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static Paragraph Styled(string? style, string text)
    {
        var paragraph = new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        if (style is not null)
        {
            paragraph.PrependChild(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        }

        return paragraph;
    }
}
