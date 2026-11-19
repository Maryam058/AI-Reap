using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
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

// Semantic understanding for clarification questions (CqValidation-v4): equivalent wording read as one
// (synonyms, phrases, the source's own aliases) without merging different entities, workflows or
// conditions; settings that settle a rule only when their link to it is written; questions built on an
// assumption the source denies; context from workflow-introducing paragraphs, Word heading numbering
// and nested tables; and the per-criterion quality assessment. Offline: no AI provider, no SQL Server,
// no network; the end-to-end case runs the real RequirementAnalysisService over InMemoryAiReapDb with a
// scripted model response, and the DOCX cases run the real DocumentTextExtractor on in-memory documents.
public class ClarificationSemanticUnderstandingTests(ITestOutputHelper output)
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
        foreach (var c in verdict.Assessment ?? []) output.WriteLine($"    assess {c.Criterion}: {c.Result} - {c.Detail}");
        return verdict;
    }

    private static string[] Codes(CandidateVerdict verdict) => verdict.Rejections.Select(r => r.Code).ToArray();

    private static RuleStatement Rule(string srs, string text)
    {
        var document = RuleContextIndex.Build(srs);
        var line = LineOf(srs, text);
        return RuleRelations.Read(document.Lines[line], line, text, document);
    }

    // ---- 1. different wording, same business rule -----------------------------------------------------

    [Fact]
    public void U1_two_passages_stating_the_same_rule_or_restriction_in_other_words_are_not_a_contradiction()
    {
        const string approved = "Leave requests must be approved by the line manager.";
        const string authorises = "The line manager authorises every leave request.";
        const string onlyManagers = "Only managers may approve leave requests.";
        const string managersOnly = "Leave requests may be approved by managers only.";
        var srs = $"""
            # Leave management

            ## 2. Approval
            {approved}
            {onlyManagers}

            ## 5. Summary
            {authorises}
            {managersOnly}
            """;

        var same = Check(srs, Candidate("contradiction",
            "Must leave requests be approved by the line manager, or does the line manager authorise them?", approved, authorises));
        Assert.Contains("same-rule", Codes(same));
        Assert.DoesNotContain("compatible-rules", Codes(same));

        // Two restrictions written differently are one restriction, not a clash (CqValidation-v3 kept it).
        var restriction = Check(srs, Candidate("contradiction",
            "May only managers approve leave requests, or may leave requests be approved by managers only?", onlyManagers, managersOnly));
        Assert.Contains("same-rule", Codes(restriction));
        Assert.Null(restriction.Relation!.Conflict);
    }

    // ---- 2. synonyms with equivalent meaning ---------------------------------------------------------

    [Fact]
    public void U2_synonyms_and_equivalent_phrases_are_read_as_one_term_in_settings_answers_and_duplicates()
    {
        Assert.Equal(ClarificationCandidateValidator.Terms("approve"), ClarificationCandidateValidator.Terms("authorise"));
        Assert.Equal(ClarificationCandidateValidator.Terms("approve"), ClarificationCandidateValidator.Terms("signed off"));
        Assert.Equal(ClarificationCandidateValidator.Terms("approve"), ClarificationCandidateValidator.Terms("granted approval"));
        Assert.Equal(ClarificationCandidateValidator.Terms("disable"), ClarificationCandidateValidator.Terms("turned off"));
        Assert.Equal(ClarificationCandidateValidator.Terms("disable"), ClarificationCandidateValidator.Terms("deactivated"));
        Assert.Equal(ClarificationCandidateValidator.Terms("mandatory"), ClarificationCandidateValidator.Terms("compulsory"));

        // A setting written "turned off" and one written "enabled": the same setting, opposite values.
        const string off = "When 'Auto-approve' is turned off, leave requests are routed to the line manager.";
        const string on = "If 'Auto-approve' is enabled, leave requests are not routed to the line manager.";
        var settings = $"""
            # Leave
            ## 3. Routing
            {off}
            ## 4. Automation
            {on}
            """;
        Assert.False(Rule(settings, off).ConditionStates.Single().Positive);
        var routed = Check(settings, Candidate("contradiction", "Are leave requests routed to the line manager or not?", off, on));
        Assert.Contains("different-conditions", Codes(routed));
        Assert.Contains("Auto-approve", routed.Rejections.First(r => r.Code == "different-conditions").Message);

        // An answer written with other words: "signed off by" answers "Who approves ...?".
        const string submit = "Employees submit leave requests through the HR portal.";
        var answered = $"""
            # Leave
            ## 2. Requests
            {submit}
            ## 6. Decisions
            Leave requests are signed off by the line manager.
            """;
        Assert.Contains("answered-in-source", Codes(Check(answered, Candidate("missing", "Who approves leave requests?", submit))));

        // A duplicate worded with an equivalent phrase.
        const string claims = "Employees submit expense claims through the finance portal.";
        var expenses = $"""
            # Expenses
            ## 2. Claims
            {claims}
            """;
        var duplicate = Check(expenses, Candidate("missing", "Who signs off expense claims?", claims),
            Existing("CQ-010", "Who approves expense claims?", expenses, claims));
        Assert.Equal("CQ-010", Assert.Single(duplicate.Rejections, r => r.Code == "duplicate").RelatedCode);
    }

    // ---- 3. similar terminology, different meanings ----------------------------------------------------

    [Fact]
    public void U3_words_that_look_alike_but_mean_different_things_are_not_merged()
    {
        Assert.NotEqual(ClarificationCandidateValidator.Terms("employer"), ClarificationCandidateValidator.Terms("employee"));
        Assert.NotEqual(ClarificationCandidateValidator.Terms("clinician"), ClarificationCandidateValidator.Terms("clinic"));
        Assert.NotEqual(ClarificationCandidateValidator.Terms("referrer"), ClarificationCandidateValidator.Terms("referral"));
        Assert.NotEqual(ClarificationCandidateValidator.Terms("applicant"), ClarificationCandidateValidator.Terms("application"));
        Assert.Equal(ClarificationCandidateValidator.Terms("employer"), ClarificationCandidateValidator.Terms("employers"));
        // Not merged unless the business meaning is the same: create / generate.
        Assert.False(ClarificationCandidateValidator.Terms("create").Overlaps(ClarificationCandidateValidator.Terms("generate")));

        // Patient / client: different terms unless the source says they are the same.
        var context = new ClarificationCandidateValidator.Context(Guid.NewGuid(), "Patients and clients register online.", []);
        Assert.NotEqual(context.TermsOf("patient"), context.TermsOf("client"));

        const string register = "Employers and employees register through the portal.";
        var srs = $"""
            # Registration
            ## 2. Accounts
            {register}
            """;
        var verdict = Check(srs, Candidate("missing", "Who approves employee registrations?", register),
            Existing("CQ-004", "Who approves employer registrations?", srs, register));
        Assert.True(verdict.Accepted);
    }

    // ---- 4. duplicate questions in completely different wording ---------------------------------------

    [Theory]
    [InlineData("Which role is responsible for authorising expense claims?")]
    [InlineData("Who has authority to approve expenses?")]
    [InlineData("Who gives final approval for expense claims?")]
    public void U4_the_same_issue_asked_in_other_words_is_one_question(string question)
    {
        const string claims = "Employees submit expense claims through the finance portal.";
        var srs = $"""
            # Expenses
            ## 2. Claims
            {claims}
            """;
        var verdict = Check(srs, Candidate("missing", question, claims), Existing("CQ-010", "Who approves expense claims?", srs, claims));

        Assert.Equal("CQ-010", Assert.Single(verdict.Rejections).RelatedCode);
    }

    [Fact]
    public void U4_a_different_business_decision_on_the_same_entity_is_not_a_duplicate()
    {
        const string claims = "Employees submit expense claims through the finance portal.";
        var srs = $"""
            # Expenses
            ## 2. Claims
            {claims}
            """;
        Assert.True(Check(srs, Candidate("missing", "Who reviews expense claims?", claims),
            Existing("CQ-010", "Who approves expense claims?", srs, claims)).Accepted);
    }

    // ---- 5. different workflows in different terminology ----------------------------------------------

    [Fact]
    public void U5_workflows_named_in_other_words_are_told_apart_in_headings_subjects_and_conditions()
    {
        var headings = """
            # Referral processing
            ## 2. Brand-new referrals
            A GP letter is mandatory.
            ## 3. Modified referrals
            A GP letter is optional.
            """;
        var byHeading = Check(headings, Candidate("contradiction", "Is a GP letter mandatory or optional?", "A GP letter is mandatory.", "A GP letter is optional."));
        Assert.Contains("different-conditions", Codes(byHeading));

        // The workflow written in the rule's own subject.
        const string brandNew = "Brand-new referrals must include a GP letter.";
        const string revised = "Revised referrals do not need a GP letter.";
        var subjects = $"""
            # Referral processing
            ## 2. GP letters
            {brandNew}
            {revised}
            """;
        Assert.Equal("create", Rule(subjects, brandNew).Scope["workflow"].Value);
        Assert.Equal("its subject", Rule(subjects, revised).Scope["workflow"].From);
        Assert.Contains("different-conditions", Codes(Check(subjects, Candidate("contradiction", "Must referrals include a GP letter?", brandNew, revised))));

        // "first submission" against "submitted again".
        const string first = "For first submissions, a GP letter is required.";
        const string again = "When a referral is submitted again, a GP letter is not required.";
        var submissions = $"""
            # Referral processing
            ## 4. Submission
            {first}
            {again}
            """;
        Assert.Equal("resubmission", Rule(submissions, again).Scope["submission"].Value);
        Assert.Contains("different-conditions", Codes(Check(submissions, Candidate("contradiction", "Is a GP letter required for referrals?", first, again))));
    }

    // ---- 6. unknown conditions are not assumed equivalent (or different) --------------------------------

    [Fact]
    public void U6_conditions_that_are_unknown_or_only_stated_on_one_side_neither_merge_nor_separate_rules()
    {
        // "from the portal" and "Urgent" can both hold: nothing makes them exclusive, the days clash.
        const string portal = "Referrals from the portal are triaged within 2 working days.";
        const string urgent = "Urgent referrals are triaged within 1 working day.";
        var srs = $"""
            # Triage
            ## 3. Targets
            {portal}
            {urgent}
            """;
        Assert.True(Check(srs, Candidate("contradiction", "Are urgent portal referrals triaged within 1 or 2 working days?", portal, urgent)).Accepted);

        // A workflow stated by one rule only is unknown for the other.
        const string fresh = "New referrals are triaged within 2 working days.";
        const string any = "Referrals are triaged within 5 working days.";
        var oneSided = $"""
            # Triage
            ## 3. Targets
            {fresh}
            {any}
            """;
        var verdict = Check(oneSided, Candidate("contradiction", "Are new referrals triaged within 2 or 5 working days?", fresh, any));
        Assert.True(verdict.Accepted);
        Assert.Null(verdict.Relation!.ExclusiveConditions);

        // Questions with different conditions are separate questions, not duplicates.
        const string claims = "Employees submit expense claims through the finance portal.";
        var expenses = $"""
            # Expenses
            ## 2. Claims
            {claims}
            """;
        var over500 = Existing("CQ-011", "Who approves expense claims over £500?", expenses, claims);
        Assert.True(Check(expenses, Candidate("missing", "Who approves expense claims for contractors?", claims), over500).Accepted);
        Assert.True(Check(expenses, Candidate("missing", "Who approves expense claims over £1,000?", claims), over500).Accepted);
    }

    // ---- 7. settings that genuinely control behaviour ---------------------------------------------------

    [Fact]
    public void U7_a_setting_the_source_says_controls_the_rule_makes_it_an_exception()
    {
        const string general = "All leave requests require line manager approval.";
        const string fastTrack = "If 'Fast track' is enabled, leave requests are approved automatically.";
        var srs = $"""
            # Leave
            ## 2. Approval
            {general}
            ## 4. Configuration
            The 'Fast track' setting controls whether line manager approval is needed.
            {fastTrack}
            """;

        var verdict = Check(srs, Candidate("contradiction", "Do all leave requests need line manager approval, or are they approved automatically?", general, fastTrack));
        var rejection = Assert.Single(verdict.Rejections, r => r.Code == "different-conditions");
        Assert.Contains("controls", rejection.Message);
        Assert.Contains($"line {LineOf(srs, "setting controls") + 1}", rejection.Message);
    }

    // ---- 8. settings that establish no relationship ----------------------------------------------------

    [Fact]
    public void U8_a_setting_nothing_links_to_the_rule_does_not_settle_the_question()
    {
        const string unsupported = "Wrist/hand referrals are not supported by Careway.";
        const string darkMode = "If 'Dark mode' is enabled, bilateral Wrist referrals are sent to Careway twice.";
        var srs = $"""
            # Careway Integration
            ## 3. Validation
            {unsupported}
            ## 7. Display
            The 'Dark mode' setting controls the colour theme.
            {darkMode}
            """;

        var verdict = Check(srs, Candidate("contradiction",
            "Should bilateral Wrist referrals be rejected as unsupported, or sent to Careway twice when 'Dark mode' is enabled?", unsupported, darkMode));
        Assert.True(verdict.Accepted);
        Assert.Null(verdict.Relation!.ExclusiveConditions);
    }

    // ---- 9. assumption-based questions -----------------------------------------------------------------

    private const string UnsupportedRule =
        "If Careway receives a referral with Thoracic Spine, Lower Limb, Head, Upper Limb or Wrist/hand, an error will be returned to Axis stating that the injury type is not supported by Careway.";

    private const string BilateralRule =
        "If Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist, call the API twice with the same information for Left and Right.";

    private const string IsCarewayRule =
        "If 'Is Careway' is disabled for the client, referrals are not sent to Careway and Axis processes the referral as normal.";

    private const string WristQuestion =
        "Should a bilateral Wrist referral be rejected as an unsupported injury type, or sent to the API twice for Left and Right?";

    private const string AssumingQuestion = "How many API calls should be made for a bilateral Wrist referral sent to Careway?";

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
    public void U9_a_question_that_assumes_what_the_source_denies_is_rejected_and_a_neutral_one_is_kept()
    {
        var assuming = Check(CarewaySrs, Candidate("missing", AssumingQuestion, BilateralRule));
        var rejection = Assert.Single(assuming.Rejections);
        Assert.Equal("unsupported-assumption", rejection.Code);
        Assert.Contains("If Careway receives a referral with Thoracic Spine", rejection.Message);
        Assert.Equal($"3. Referral validation (line {LineOf(CarewaySrs, "Thoracic Spine") + 1})", rejection.Location);

        Assert.True(Check(CarewaySrs, Candidate("contradiction", WristQuestion, UnsupportedRule, BilateralRule)).Accepted);
        Assert.DoesNotContain("unsupported-assumption",
            Codes(Check(CarewaySrs, Candidate("ambiguous", "What happens when a bilateral Wrist referral is sent to Careway?", BilateralRule))));

        // A denial that holds only under a setting ("If 'Is Careway' is disabled ...") says nothing about
        // the other cases, so a question about them assumes nothing it denies.
        var scoped = """
            # Careway Integration
            ## 4. Bilateral referrals
            For bilateral Wrist referrals, call the API twice.
            ## 5. Client configuration
            If 'Is Careway' is disabled, Wrist referrals are not accepted by Careway.
            """;
        Assert.DoesNotContain("unsupported-assumption",
            Codes(Check(scoped, Candidate("missing", "How many API calls are made for bilateral Wrist referrals?", "For bilateral Wrist referrals, call the API twice."))));
    }

    // ---- 10. genuinely missing information ------------------------------------------------------------

    [Fact]
    public void U10_a_genuine_gap_is_kept_with_an_assessment_of_every_check()
    {
        const string lockRule = "The system shall lock the account after repeated failed login attempts.";
        var srs = $"""
            # Account security
            ## 3. Login
            {lockRule}
            ## 4. Notifications
            Users are notified by email when their account is locked.
            """;

        var verdict = Check(srs, Candidate("missing", "How many failed login attempts lock the account?", lockRule));
        Assert.True(verdict.Accepted);
        var assessment = verdict.Assessment!.ToDictionary(c => c.Criterion);
        Assert.Equal(["question-form", "source-evidence", "business-impact", "information-gap", "unsupported-assumption", "duplicate", "cross-reference", "condition-clarity"],
            verdict.Assessment!.Select(c => c.Criterion));
        Assert.All(verdict.Assessment!, c => Assert.NotEqual("failed", c.Result));
        Assert.Contains($"line {LineOf(srs, lockRule) + 1}", assessment["source-evidence"].Detail);
        Assert.Equal("not-applicable", assessment["cross-reference"].Result);
    }

    // ---- 11. context from explanatory paragraphs --------------------------------------------------------

    [Fact]
    public void U11_a_paragraph_introducing_the_workflow_is_inherited_and_a_plain_paragraph_is_not()
    {
        static string Srs(string intakeIntro) => $"""
            # Referral processing

            ## 4. Processing
            This section describes how amended referrals are processed.
            A GP letter is optional.

            ## 5. Intake
            {intakeIntro}
            A GP letter is mandatory.
            """;

        var plain = Srs("Referrals are received from GPs and hospitals.");
        var optional = Rule(plain, "A GP letter is optional.");
        var scope = Assert.Single(optional.Context, c => c.Kind == "scope");
        Assert.Equal("This section describes how amended referrals are processed.", scope.Text);
        Assert.Equal(LineOf(plain, "This section describes"), scope.Line);
        Assert.Equal("update", optional.Scope["workflow"].Value);

        // The plain explanatory paragraph says nothing about which referrals the rule covers: unknown.
        var mandatory = Rule(plain, "A GP letter is mandatory.");
        Assert.DoesNotContain(mandatory.Context, c => c.Kind is "scope" or "lead-in");
        Assert.True(Check(plain, Candidate("contradiction", "Is a GP letter mandatory or optional for referrals?",
            "A GP letter is optional.", "A GP letter is mandatory.")).Accepted);

        var introduced = Srs("This section covers brand-new referrals.");
        Assert.Contains("different-conditions", Codes(Check(introduced, Candidate("contradiction", "Is a GP letter mandatory or optional for referrals?",
            "A GP letter is optional.", "A GP letter is mandatory."))));
    }

    // ---- 12. Word heading styles ---------------------------------------------------------------------

    [Fact]
    public async Task U12_numbered_Word_heading_styles_give_the_heading_hierarchy_rules_inherit()
    {
        var bytes = DocxFixtureBuilder.NumberedHeadings(
        [
            (1, "Referral processing", "Referrals are validated before they are sent."),
            (2, "New referrals", "A GP letter is mandatory."),
            (2, "Amended referrals", "A GP letter is optional.")
        ], []);
        var srs = (await new DocumentTextExtractor().ExtractDocumentAsync(bytes, "srs.docx")).Text;
        output.WriteLine(srs);

        var optional = Rule(srs, "A GP letter is optional.");
        var headings = optional.Context.Where(c => c.Kind == "heading").ToList();
        Assert.Equal(["1.\tReferral processing", "1.2\tAmended referrals"], headings.Select(h => h.Text));
        Assert.Equal([1, 2], headings.Select(h => h.Level));
        Assert.DoesNotContain(optional.Context, c => c.Text.Contains("New referrals"));

        Assert.Contains("different-conditions", Codes(Check(srs, Candidate("contradiction", "Is a GP letter mandatory or optional?",
            "A GP letter is mandatory.", "A GP letter is optional."))));
    }

    // ---- 13. nested tables and table headings -------------------------------------------------------------

    [Fact]
    public async Task U13_a_nested_table_cell_is_no_condition_and_the_table_caption_and_heading_are_inherited()
    {
        var bytes = Docx(body =>
        {
            body.Append(Para("4. Bilateral rules"));
            body.Append(Para("Table 4: Bilateral processing"));
            body.Append(new Table(
                Row(Cell("Body Part"), Cell("Sides"), Cell("Action")),
                Row(Cell("Knee"), new TableCell(new Table(Row(Cell("Side"), Cell("Calls")), Row(Cell("Left"), Cell("1")), Row(Cell("Bilateral"), Cell("2"))), Para(string.Empty)), Cell("Call the API once per side")),
                Row(Cell("Wrist"), Cell("Bilateral"), Cell("Return an error"))));
        });
        var srs = (await new DocumentTextExtractor().ExtractDocumentAsync(bytes, "table.docx")).Text;
        output.WriteLine(srs);
        Assert.Contains("Knee | Side, Calls; Left, 1; Bilateral, 2 | Call the API once per side", srs);

        var document = RuleContextIndex.Build(srs);
        var knee = document.RowAt(LineOf(srs, "Knee |"))!;
        Assert.Equal("Body Part = Knee", knee.Condition);          // which nested row applies is unknown
        Assert.Equal("Call the API once per side", knee.Outcome);
        Assert.Equal("Body Part = Wrist and Sides = Bilateral", document.RowAt(LineOf(srs, "Wrist |"))!.Condition);

        var context = document.ContextOf(LineOf(srs, "Knee |"));
        Assert.Contains(context, c => c.Kind == "heading" && c.Text == "4. Bilateral rules");
        Assert.Contains(context, c => c.Kind == "table-title" && c.Text == "Table 4: Bilateral processing");
        Assert.Contains(context, c => c.Kind == "table-header" && c.Text == "Body Part | Sides | Action");
    }

    // ---- 14. different entities with similar names ---------------------------------------------------

    [Fact]
    public void U14_rules_about_different_entities_with_similar_names_are_not_a_contradiction_unless_the_source_links_them()
    {
        const string clinician = "Clinician accounts are deleted after 30 days of inactivity.";
        const string clinic = "Clinic accounts are deleted after 90 days of inactivity.";
        var accounts = $"""
            # Accounts
            ## 6. Retention
            {clinician}
            {clinic}
            """;
        Assert.Contains("different-subjects", Codes(Check(accounts, Candidate("contradiction",
            "Are accounts deleted after 30 or 90 days of inactivity?", clinician, clinic))));

        const string patient = "Patient records are kept for 8 years.";
        const string client = "Client records are kept for 6 years.";
        static string Records(string glossary) => $"""
            # Records
            ## 1. Terms
            {glossary}
            ## 6. Retention
            {patient}
            {client}
            """;
        const string question = "Are records kept for 8 or 6 years?";

        // Without a statement linking them, patient and client are different entities.
        Assert.Contains("different-subjects", Codes(Check(Records("Records are stored securely."), Candidate("contradiction", question, patient, client))));

        // The source says they are the same: a genuine contradiction about one entity.
        var linked = Records("In this document, a Patient (also referred to as Client) is a person receiving treatment.");
        var aliases = TermAliases.From(linked);
        Assert.Equal(LineOf(linked, "also referred to as"), Assert.Single(aliases.Pairs).Line);
        Assert.True(Check(linked, Candidate("contradiction", question, patient, client)).Accepted);
    }

    // ---- 15. genuine contradictions are kept ------------------------------------------------------------

    [Fact]
    public void U15_genuine_contradictions_in_other_words_or_under_the_same_setting_are_not_rejected()
    {
        const string two = "Leave requests must be approved within 2 working days.";
        const string five = "Leave requests must be authorised within 5 working days.";
        var days = $"""
            # Leave
            ## 2. Approval
            {two}
            ## 7. Service levels
            {five}
            """;
        var verdict = Check(days, Candidate("contradiction", "Must leave requests be approved within 2 or 5 working days?", two, five));
        Assert.True(verdict.Accepted);
        Assert.Contains("approved", verdict.Relation!.SharedSubject);

        // "turned on" and "enabled" are the same setting value: the same case, outcomes that clash.
        const string immediate = "When 'Auto-approve' is turned on, leave requests are approved immediately.";
        const string review = "If 'Auto-approve' is enabled, leave requests must not be approved without the line manager's review.";
        var setting = $"""
            # Leave
            ## 4. Automation
            {immediate}
            ## 6. Controls
            {review}
            """;
        Assert.True(Check(setting, Candidate("contradiction",
            "Are leave requests approved immediately or only after the line manager's review when 'Auto-approve' is on?", immediate, review)).Accepted);

        // The original Careway pair stays one question.
        Assert.True(Check(CarewaySrs, Candidate("contradiction", WristQuestion, UnsupportedRule, BilateralRule)).Accepted);
    }

    // ---- Careway end to end ---------------------------------------------------------------------------

    [Fact]
    public async Task Careway_end_to_end_saves_one_assessed_question_and_rejects_the_assumption_the_setting_and_the_reworded_duplicate()
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "CQ semantic project", CreatedByUserId = "phase0-offline-user" };
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
            ["missingInformation"] = new JsonArray(
                Question("contradiction", "Bilateral Wrist referrals", WristQuestion, UnsupportedRule, BilateralRule),
                Question("missing", "Bilateral Wrist API calls", AssumingQuestion, BilateralRule),
                Question("contradiction", "Referral routing", "Are referrals sent to Careway through the referral API or processed by Axis as normal?",
                    "Axis sends injury referrals to Careway through the Careway referral API.", IsCarewayRule),
                Question("ambiguous", "Wrist rule precedence", "Which of the two Wrist rules takes precedence when Body Side is Bilateral?",
                    UnsupportedRule, BilateralRule))
        }.ToJsonString());

        var result = await new RequirementAnalysisService(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(),
            new BatchModeOff(), NullLogger<RequirementAnalysisService>.Instance).AnalyzeAsync(source.Id);
        foreach (var r in result.RejectedCandidates!) output.WriteLine($"REJECTED {r.Question}: {string.Join("; ", r.Reasons.Select(x => $"{x.Code}: {x.Message}"))}");

        var saved = Assert.Single(InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion));
        var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(saved.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(WristQuestion, payload.Question);
        Assert.NotNull(payload.RuleComparison);
        var assessment = payload.Assessment!.ToDictionary(c => c.Criterion);
        Assert.All(payload.Assessment!, c => Assert.NotEqual("failed", c.Result));
        Assert.Equal("passed", assessment["cross-reference"].Result);
        Assert.Contains("one rule refuses", assessment["cross-reference"].Detail);

        Assert.Contains(result.RejectedCandidates, r => r.Question == AssumingQuestion && r.Reasons.Any(x => x.Code == "unsupported-assumption"));
        Assert.Contains(result.RejectedCandidates, r => r.Question.StartsWith("Are referrals sent") && r.Reasons.Any(x => x.Code == "different-conditions"));
        Assert.Contains(result.RejectedCandidates, r => r.Question.StartsWith("Which of the two") && r.Reasons.Any(x => x.Code == "duplicate"));
    }

    // ---- helpers --------------------------------------------------------------------------------------

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

    private static Paragraph Para(string text) => new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static TableCell Cell(string text) => new(Para(text));

    private static TableRow Row(params TableCell[] cells) => new(cells);
}
