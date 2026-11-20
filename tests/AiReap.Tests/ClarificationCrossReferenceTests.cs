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

// Cross-referenced clarification questions (CqValidation-v2, RuleRelations): before a question that sets
// passages against each other is accepted, the passages are compared as rules - shared subject,
// conditions, outcomes - and the rest of the source is searched for a passage that settles them. Plus
// the semantic duplicate rules. Offline: no AI provider, no SQL Server, no network; the end-to-end case
// runs the real RequirementAnalysisService over InMemoryAiReapDb with a scripted model response.
public class ClarificationCrossReferenceTests(ITestOutputHelper output)
{
    // The two Careway passages exactly as given, plus the 'Is Careway' client rule.
    private const string UnsupportedRule =
        "If Careway receives a referral with Thoracic Spine, Lower Limb, Head, Upper Limb or Wrist/hand, an error will be returned to Axis stating that the injury type is not supported by Careway.";

    private const string BilateralRule =
        "If Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist, call the API twice with the same information for Left and Right.";

    private const string IsCarewayRule =
        "If 'Is Careway' is disabled for the client, referrals are not sent to Careway and Axis processes the referral as normal.";

    private static string CarewaySrs(string extraSection = "") => $"""
        # Careway Integration - Software Requirements Specification

        ## 1. Overview
        Axis sends injury referrals to Careway through the Careway referral API.

        ## 3. Referral validation
        {UnsupportedRule}

        ## 4. Bilateral referrals
        {BilateralRule}

        ## 5. Client configuration
        {IsCarewayRule}
        {extraSection}
        """;

    private const string WristQuestion =
        "Should a bilateral Wrist referral be rejected as an unsupported injury type, or sent to the API twice for Left and Right?";

    private static ClarificationCandidateValidator.Context Context(string srs, params ExistingClarification[] existing) =>
        new(Guid.NewGuid(), srs, existing);

    private static ClarificationCandidate Candidate(string kind, string topic, string question, params string[] evidence) =>
        new(topic, question, "Checked the related sections; they do not settle it.", kind,
            "Decides how the requirement is implemented and tested.", evidence);

    private void Report(CandidateVerdict verdict)
    {
        output.WriteLine($"{(verdict.Accepted ? "ACCEPTED" : "REJECTED")} [{verdict.Kind}] {verdict.Candidate.Question}");
        foreach (var r in verdict.Rejections) output.WriteLine($"    {r.Code}: {r.Message}");
        if (verdict.Relation is { } rel)
        {
            output.WriteLine($"    shared subject: {string.Join(", ", rel.SharedSubject)}; conflict: {rel.Conflict ?? "none"}; " +
                             $"exclusive: {rel.ExclusiveConditions ?? "none"}; resolution: {rel.Resolution?.Text ?? "none"}");
        }
    }

    private CandidateVerdict Check(ClarificationCandidateValidator.Context context, ClarificationCandidate candidate)
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(context, candidate);
        Report(verdict);
        return verdict;
    }

    // ==== Mandatory Wrist/hand regression ===============================================================

    [Fact]
    public void Wrist_both_rules_are_read_with_their_conditions_and_outcomes()
    {
        var unsupported = RuleRelations.Read(UnsupportedRule);
        var bilateral = RuleRelations.Read(BilateralRule);

        // The unsupported-injury rule: its condition names Wrist/hand, its outcome refuses.
        Assert.Contains("Wrist/hand", unsupported.Condition);
        Assert.Contains("not supported by Careway", unsupported.Outcome);
        Assert.True(unsupported.NegativeOutcome);

        // The bilateral API rule: a separate behaviour under a specific condition (Body Side = Bilateral).
        Assert.Contains("Wrist", bilateral.Condition);
        Assert.Contains("call the API twice", bilateral.Outcome);
        Assert.False(bilateral.NegativeOutcome);
        var side = Assert.Single(bilateral.ConditionStates);
        Assert.Equal("Body Side", side.Attribute);
        Assert.Equal("Bilateral", side.Value);

        var relation = RuleRelations.Compare(unsupported, bilateral, []);
        Assert.Equal(["Wrist"], relation.SharedSubject);  // the shared entity
        Assert.Null(relation.ExclusiveConditions);         // one referral can meet both conditions
        Assert.NotNull(relation.Conflict);                 // one refuses, the other calls the API
        Assert.Null(relation.Resolution);
    }

    [Fact]
    public void Wrist_unresolved_relationship_produces_one_focused_question_citing_both_passages()
    {
        var srs = CarewaySrs();
        var lines = SourceReferenceIndex.Lines(srs);
        var verdict = Check(Context(srs), Candidate("contradiction", "Bilateral Wrist referrals", WristQuestion,
            "an error will be returned to Axis stating that the injury type is not supported by Careway",
            "If Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist"));

        Assert.True(verdict.Accepted);
        Assert.Equal(2, verdict.Evidence.Count);
        Assert.Equal(Array.FindIndex(lines, l => l.Contains("Thoracic Spine")), verdict.Evidence[0].Line);
        Assert.Equal(Array.FindIndex(lines, l => l.Contains("Body Side = Bilateral")), verdict.Evidence[1].Line);
        Assert.Equal(["3. Referral validation", "4. Bilateral referrals"], verdict.RequirementRefs);
        Assert.Equal(["Wrist"], verdict.Relation!.SharedSubject);
    }

    [Fact]
    public void Wrist_the_same_question_labelled_as_an_ambiguous_interaction_is_also_kept()
    {
        var verdict = Check(Context(CarewaySrs()), Candidate("ambiguous", "Rule order for Wrist",
            "Which rule applies to a bilateral Wrist referral: the unsupported injury type error or the double API call?",
            UnsupportedRule, BilateralRule));

        Assert.True(verdict.Accepted);
        Assert.NotNull(verdict.Relation);
    }

    [Fact]
    public void Wrist_a_question_is_not_raised_merely_because_both_passages_mention_Wrist()
    {
        // A "conflict" between the bilateral rule and a rule that only describes another behaviour for
        // Wrist referrals: both can happen, so there is nothing to clarify.
        var srs = CarewaySrs("""

            ## 6. Triage
            Wrist referrals are triaged by the hand therapy team.
            """);
        var verdict = Check(Context(srs), Candidate("contradiction", "Wrist handling",
            "Are bilateral Wrist referrals triaged by the hand therapy team or sent to the API twice?",
            "Wrist referrals are triaged by the hand therapy team.", BilateralRule));

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Rejections, r => r.Code == "compatible-rules");
    }

    [Fact]
    public void Wrist_a_passage_stating_how_the_rules_relate_means_no_question()
    {
        var srs = CarewaySrs("""

            ## 6. Processing order
            The bilateral rule does not apply to injury types that Careway does not support.
            """);
        var verdict = Check(Context(srs), Candidate("contradiction", "Bilateral Wrist referrals", WristQuestion,
            UnsupportedRule, BilateralRule));

        Assert.False(verdict.Accepted);
        var resolved = Assert.Single(verdict.Rejections, r => r.Code == "resolved-in-source");
        Assert.Contains("6. Processing order", resolved.Location);
        Assert.Contains("does not apply to injury types", resolved.Message);
    }

    [Fact]
    public void Wrist_the_Is_Careway_rule_is_read_for_its_scope_and_does_not_by_itself_settle_the_question()
    {
        // Investigated, not ignored: the client rule is a condition on 'Is Careway' being disabled ...
        var rule = RuleRelations.Read(IsCarewayRule);
        var flag = Assert.Single(rule.ConditionStates);
        Assert.Contains("Careway", flag.Attribute);
        Assert.False(flag.Positive);

        // ... which says nothing about bilateral processing, so it does not resolve the Wrist question;
        // cited as a third passage it is kept as evidence for traceability.
        var verdict = Check(Context(CarewaySrs()), Candidate("contradiction", "Bilateral Wrist referrals", WristQuestion,
            UnsupportedRule, BilateralRule, IsCarewayRule));
        Assert.True(verdict.Accepted);
        Assert.Equal(3, verdict.Evidence.Count);
        Assert.Contains(verdict.RequirementRefs, r => r == "5. Client configuration");
    }

    [Fact]
    public void Wrist_rules_scoped_to_opposite_Is_Careway_settings_never_meet_so_no_question()
    {
        const string enabled = "If 'Is Careway' is enabled and Careway receives a referral with Thoracic Spine, Lower Limb, Head, Upper Limb or Wrist/hand, an error will be returned to Axis stating that the injury type is not supported by Careway.";
        const string disabled = "If 'Is Careway' is disabled, Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist, call the API twice with the same information for Left and Right.";
        var srs = $"""
            # Careway Integration

            ## 3. Careway clients
            {enabled}

            ## 4. Other clients
            {disabled}
            """;

        var verdict = Check(Context(srs), Candidate("contradiction", "Bilateral Wrist referrals", WristQuestion, enabled, disabled));

        Assert.False(verdict.Accepted);
        var rejection = Assert.Single(verdict.Rejections, r => r.Code == "different-conditions");
        Assert.Contains("Is Careway", rejection.Message);
    }

    [Fact]
    public async Task Wrist_end_to_end_the_analysis_saves_exactly_one_evidence_backed_question()
    {
        var srs = CarewaySrs();
        var (db, source) = Seed(srs);
        // What a model might return after reading the document: the real issue twice (once per section it
        // read), and a keyword-only "conflict".
        var ai = new ScriptedAiChatClient(Response(
            Question("contradiction", "Bilateral Wrist referrals", WristQuestion,
                "an error will be returned to Axis stating that the injury type is not supported by Careway",
                "If Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist"),
            Question("contradiction", "Wrist support", "Does Careway support Wrist referrals when Body Side is Bilateral?",
                "If Body Side = Bilateral and Body Part is Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist",
                "Thoracic Spine, Lower Limb, Head, Upper Limb or Wrist/hand"),
            Question("contradiction", "Referral routing", "Are referrals sent to Careway through the referral API or processed by Axis as normal?",
                "Axis sends injury referrals to Careway through the Careway referral API.", IsCarewayRule)));

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);
        foreach (var r in result.RejectedCandidates ?? [])
        {
            output.WriteLine($"REJECTED \"{r.Question}\" -> {string.Join("; ", r.Reasons.Select(x => $"{x.Code}: {x.Message}"))}");
        }

        var saved = Assert.Single(InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion));
        var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(saved.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(WristQuestion, payload.Question);
        Assert.Equal(2, payload.SourceEvidence!.Count);
        Assert.All(payload.SourceEvidence, e => Assert.True(e.Line >= 0 && e.ExcerptIsQuote));

        Assert.Equal(2, result.RejectedCandidates!.Count);
        Assert.Contains(result.RejectedCandidates, r => r.Question.StartsWith("Does Careway") && r.Reasons.Any(x => x.Code == "duplicate"));
        Assert.Contains(result.RejectedCandidates, r => r.Question.StartsWith("Are referrals") && r.Reasons.Any(x => x.Code == "different-conditions"));
    }

    // ==== The ten scenarios ===============================================================================

    private const string ReferralSrs = """
        # Referral Service - Software Requirements Specification

        ## 2. Acceptance
        When a referral is accepted, Careway sends an acknowledgement to Axis.
        If the referral contains a date of birth, the system calculates the claimant's age.

        ## 3. Validation
        If the referral is missing the date of birth, an error is returned to Axis.

        ## 4. Sides
        If Body Side = Left, call the API once with Left.
        If Body Side = Bilateral, call the API twice with Left and Right.

        ## 5. Expenses
        Expense claims are routed for approval.
        Claims above the approval threshold require director sign-off.
        The system must notify the clinician after completion.

        ## 6. Scheduling
        When a referral is accepted, the system creates an appointment record for the claimant.
        Managers can delete approved leave requests.

        ## 7. Roles
        Finance officers approve all expense claims.

        ## 8. Retention
        Approved leave requests must not be deleted.
        """;

    [Fact]
    public void S1_different_behaviours_for_the_same_entity_that_can_coexist_give_no_question()
    {
        var verdict = Check(Context(ReferralSrs), Candidate("contradiction", "Accepted referrals",
            "When a referral is accepted, should Careway send an acknowledgement or create an appointment record?",
            "When a referral is accepted, Careway sends an acknowledgement to Axis.",
            "When a referral is accepted, the system creates an appointment record for the claimant."));

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Rejections, r => r.Code == "compatible-rules");
    }

    [Fact]
    public void S2_an_explicit_contradiction_under_identical_conditions_gives_a_question()
    {
        var verdict = Check(Context(ReferralSrs), Candidate("contradiction", "Deleting approved leave",
            "May managers delete approved leave requests, or must approved leave requests never be deleted?",
            "Managers can delete approved leave requests.", "Approved leave requests must not be deleted."));

        Assert.True(verdict.Accepted);
        Assert.NotNull(verdict.Relation!.Conflict);
    }

    [Fact]
    public void S3_information_given_in_another_section_answers_the_question()
    {
        var verdict = Check(Context(ReferralSrs), Candidate("missing", "Expense approver",
            "Who approves expense claims?", "Expense claims are routed for approval."));

        Assert.False(verdict.Accepted);
        var answered = Assert.Single(verdict.Rejections, r => r.Code == "answered-in-source");
        Assert.Contains("7. Roles", answered.Location);
    }

    [Fact]
    public void S4_a_genuinely_missing_business_rule_gives_a_question()
    {
        var verdict = Check(Context(ReferralSrs), Candidate("missing", "Approval threshold",
            "What is the approval threshold amount above which claims require director sign-off?",
            "Claims above the approval threshold require director sign-off."));

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void S5_a_rule_with_several_reasonable_readings_gives_a_question()
    {
        var verdict = Check(Context(ReferralSrs), Candidate("ambiguous", "Completion event",
            "Which completion event triggers the clinician notification, completion of the assessment or completion of the report?",
            "The system must notify the clinician after completion."));

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void S6_semantically_identical_questions_from_different_sections_give_one_question()
    {
        // No answering passage in this source, so only the duplicate rule decides.
        const string srs = """
            # Expenses

            ## 2. Claims
            Expense claims are routed for approval.

            ## 9. Workflow
            Each expense claim moves to Approved once it has been authorised.
            """;
        var verdicts = ClarificationCandidateValidator.Validate(Context(srs),
        [
            Candidate("missing", "Expense approver", "Who approves expense claims?", "Expense claims are routed for approval."),
            Candidate("missing", "Authorising role", "Which role or person is responsible and accountable for authorising expense claims?",
                "Each expense claim moves to Approved once it has been authorised."),
            // Same words, a different question: it asks when, not who - kept.
            Candidate("missing", "Approval deadline", "When must expense claims be approved?", "Expense claims are routed for approval.")
        ]);
        verdicts.ForEach(Report);

        Assert.True(verdicts[0].Accepted);
        Assert.Contains(verdicts[1].Rejections, r => r.Code == "duplicate");
        Assert.True(verdicts[2].Accepted, string.Join("; ", verdicts[2].Rejections.Select(r => r.Message)));
    }

    [Fact]
    public void S7_the_same_entity_under_different_conditions_gives_no_question()
    {
        var context = Context(ReferralSrs);
        var sides = Check(context, Candidate("contradiction", "API calls per side",
            "Should the API be called once with Left or twice with Left and Right?",
            "If Body Side = Left, call the API once with Left.", "If Body Side = Bilateral, call the API twice with Left and Right."));
        var birth = Check(context, Candidate("contradiction", "Date of birth",
            "Should a referral's date of birth be used to calculate the claimant's age, or should an error be returned to Axis?",
            "If the referral contains a date of birth, the system calculates the claimant's age.",
            "If the referral is missing the date of birth, an error is returned to Axis."));

        Assert.Contains(sides.Rejections, r => r.Code == "different-conditions");
        Assert.Contains(birth.Rejections, r => r.Code == "different-conditions");
    }

    private const string CancellationSrs = """
        # Leave Management

        ## 2. Cancellation
        Employees may cancel a request up to 24 hours before the leave starts.

        ## 3. Policies
        Requests can be cancelled up to 48 hours before the start date.
        Cancelled requests are returned to the employee's balance.
        """;

    [Fact]
    public void S8_an_explicit_exception_that_resolves_the_apparent_contradiction_gives_no_question()
    {
        var srs = CancellationSrs + "\nThe 48-hour limit applies only to requests for more than five days of leave.\n";
        var verdict = Check(Context(srs), Candidate("contradiction", "Cancellation deadline",
            "Is the cancellation deadline 24 hours or 48 hours before the leave starts?",
            "cancel a request up to 24 hours before the leave starts", "Requests can be cancelled up to 48 hours before the start date"));

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Rejections, r => r.Code == "resolved-in-source" && r.Message.Contains("applies only"));
    }

    [Fact]
    public void S9_a_conflict_that_cross_referencing_does_not_resolve_gives_exactly_one_evidence_backed_question()
    {
        var verdicts = ClarificationCandidateValidator.Validate(Context(CancellationSrs),
        [
            Candidate("contradiction", "Cancellation deadline", "Is the cancellation deadline 24 hours or 48 hours before the leave starts?",
                "cancel a request up to 24 hours before the leave starts", "Requests can be cancelled up to 48 hours before the start date"),
            // The same conflict seen again from the other section, in other words.
            Candidate("ambiguous", "Policy window", "Which window governs late withdrawals of leave?",
                "Requests can be cancelled up to 48 hours before the start date", "Employees may cancel a request up to 24 hours before the leave starts")
        ]);
        verdicts.ForEach(Report);

        Assert.True(verdicts[0].Accepted);
        Assert.Equal(2, verdicts[0].Evidence.Count);
        Assert.Null(verdicts[0].Relation!.Resolution); // "Cancelled requests are returned ..." settles nothing
        Assert.Contains(verdicts[1].Rejections, r => r.Code == "duplicate");
    }

    [Fact]
    public void S10_a_question_already_answered_gives_no_new_question()
    {
        var existing = new ExistingClarification("CQ-0004", "Who is the approver for expense claims?", "Expense approver", "Answered",
            new HashSet<int>(), "missing", Answer: "The finance director approves every expense claim.");
        const string srs = """
            # Expenses

            ## 2. Claims
            Expense claims are routed for approval.
            """;
        var verdict = Check(Context(srs, existing), Candidate("missing", "Claim authoriser",
            "Which role is responsible for authorising expense claims?", "Expense claims are routed for approval."));

        Assert.False(verdict.Accepted);
        var answered = Assert.Single(verdict.Rejections, r => r.Code == "already-answered");
        Assert.Equal("CQ-0004", answered.RelatedCode);
    }

    // ==== guards on the cross-reference checks ==========================================================

    [Fact]
    public void A_contradiction_between_passages_that_share_no_subject_is_rejected()
    {
        var verdict = Check(Context(ReferralSrs), Candidate("contradiction", "Unrelated",
            "Do finance officers approve expense claims when Body Side is Left?",
            "Finance officers approve all expense claims.", "If Body Side = Left, call the API once with Left."));

        Assert.Contains(verdict.Rejections, r => r.Code == "no-shared-subject");
    }

    [Fact]
    public void A_restriction_against_a_permission_is_still_a_conflict()
    {
        const string srs = """
            # Bookings

            ## 2. Reception
            Receptionists may delete bookings that were created by mistake.

            ## 3. Administration
            Only administrators may delete bookings.
            """;
        var verdict = Check(Context(srs), Candidate("contradiction", "Deleting bookings",
            "May receptionists delete bookings, or only administrators?",
            "Receptionists may delete bookings that were created by mistake", "Only administrators may delete bookings"));

        Assert.True(verdict.Accepted);
    }

    // ---- end-to-end helpers (as ClarificationQualityTests) ---------------------------------------------

    private static (InMemoryAiReapDb Db, RequirementSource Source) Seed(string rawText)
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "CQ cross-reference project", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = rawText,
            CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        return (db, source);
    }

    private static RequirementAnalysisService Analysis(InMemoryAiReapDb db, IAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(), new BatchModeOff(),
            NullLogger<RequirementAnalysisService>.Instance);

    private static JsonObject Question(string kind, string topic, string question, params string[] evidence) => new()
    {
        ["kind"] = kind, ["topic"] = topic, ["question"] = question,
        ["reason"] = "Checked every section that mentions it; nothing says how the rules relate.",
        ["impact"] = "Decides how the integration is implemented and tested.",
        ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e).ToArray())
    };

    private static string Response(params JsonObject[] questions) => new JsonObject
    {
        ["actors"] = new JsonArray("Axis", "Careway"),
        ["capabilities"] = new JsonArray("Validate referral", "Send bilateral referral"),
        ["dataElements"] = new JsonArray("Referral", "Body Side", "Body Part"),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(questions.Cast<JsonNode>().ToArray())
    }.ToJsonString();
}
