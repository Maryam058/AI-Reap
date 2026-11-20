using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Files;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Files;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// Accurate, evidence-based clarification questions (CqValidation-v5), regression-tested on the real
// Careway Referral Pathways SRS (Fixtures/Careway, a Word file read by the app's own DocumentTextExtractor)
// and on small synthetic sources. Each test checks the question AND the passages attached to it:
//   - sections come from the document's table of contents, so a passage is labelled with the section it
//     is in, never a list item above it ("2. Does Axis want a confirmation and error email?");
//   - each passage must itself be about the question (EvidenceSupport); one sharing only the document's
//     general vocabulary is not attached, and a question citing only passages about something else
//     (evidence-misses-subject) is not accepted;
//   - options the source lists for one open decision are one question (DecisionBlocks), and the question
//     kept takes over its duplicate's supporting passages;
//   - an open note ("Consider how ...") is never an answer, and "exception" in the sense of an error case
//     never settles two rules;
//   - a genuine conflict asked one-sidedly is held for review, not lost.
// Offline: no AI provider, no SQL Server, no network.
public class ClarificationAccuracyTests(ITestOutputHelper output)
{
    // ---- the Careway SRS, as the app extracts it ---------------------------------------------------------

    private static readonly Lazy<Task<ExtractedDocument>> CarewayDocument = new(async () =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Careway", "careway-referral-pathways-v0.8.docx");
        return await new DocumentTextExtractor().ExtractDocumentAsync(await File.ReadAllBytesAsync(path), "careway-referral-pathways-v0.8.docx");
    });

    private static async Task<string> Careway() => (await CarewayDocument.Value).Text;

    // The 0-based line of the source that holds the fragment (the first, unless an occurrence is given).
    private static int LineOf(string source, string fragment, int occurrence = 0)
    {
        var lines = SourceReferenceIndex.Lines(source);
        var found = Enumerable.Range(0, lines.Length).Where(i => lines[i].Contains(fragment, StringComparison.Ordinal)).ToList();
        Assert.True(found.Count > occurrence, $"\"{fragment}\" is not in the source {occurrence + 1} time(s).");
        return found[occurrence];
    }

    // The Careway passages the questions rest on, word for word as the document writes them.
    private const string DocTypesVsStatus = "If only one enrolment found, then specify rules for different document types vs enrolment status";
    private const string DocumentCategory = "Careway Rules: Each document saved to the enrolment, need to map the correct document category";
    private const string EmailNotification = "Careway notifies providers (physiotherapists, occupational therapists, SEM physicians/registrars and surgeons) if they have been added to the IDT for a particular patient via email and via the portal";
    private const string ApiDelivery = "Careway want to receive documentation via API.";
    private const string EmailCopies = "This would include copies to info@careway.co.nz (or API in future)";
    private const string IdtConsideration = "need consideration as to how Axis/Specialists assigns and sends IDT physiotherapist board number field data to Careway";
    private const string TwoLetters = "There are times when Axis/Specialist would need to write 2x referral letters";
    private const string CcOption = "Any new physiotherapist that needs to be linked/added to the patient’s IDT could be selected at the Letter writing stage";
    private const string NzmcAdmin = "Axis to provide a dummy NZMC number for the Referral Admin user to help with mapping";
    private const string ServiceCodes = "Service code will determine how the batching to Careway works.";
    private const string InvoiceBatch = "Indici should automatically send/batch invoice(s) relevant to the Careway system";
    private const string PriorityField = "Priority: Urgent, Medium, Routine, Low (Medium is currently assigned to Careway)";
    private const string OutcomeExtraction = "How will Careway extract outcome tool data from our letters into their outcome tool section?";
    private const string OutcomeFormat = "Careway to send Outcome Score format to Indici for recreation in Axis/UniSports system";
    private const string CheckboxLocation = "Suggested location of ‘Is Careway’ checkbox to update linked Referral form";
    private const string ArtpConsider = "Consider how to send ARTP in addition to clinic letter (currently only 1 letter can be produced per appointment)";
    private const string ArtpEmail = "The ARTP could be emailed to info@careway when completed";
    private const string ReferralLetterToCareway = "the indici PMS should automatically send the referral letter to the referred specialist AND the Careway system";
    private const string SpecialCaseEnroller = "This action would likely note that the provider enrolling the patient was the logged in user i.e. the administrator, not the IDT member.";
    private const string SpecialCaseFunder = "Consideration for how to amend funder from ACC  ACC-Careway if decision is changed after an appointment.";
    private const string DraftEnrolment = "create and save the draft enrolment with the provider as the Default IDT Member";
    private const string BilateralWrist = "If the Body Side = Bilateral and Body Part is either Knee, Shoulder-Other, Shoulder Instability, Ankle or Wrist – call the API twice with the same information for Left and Right";
    private const string WristUnsupported = "If Careway received a referral with Thoracic Spine, Lower Limb, Head, Upper Limb, Wrist/hand - an error will be returned to Axis saying 'Injury type not supported by Careway'";
    private const string ConsentUrl = "this will trigger their (patients’) unique consent URL/QR codes to be sent to both the patient’s email and SMS from the Careway platform";

    // The corrected questions (the "after" of each Careway example).
    private const string Cq003 = "What rules should apply to each document type for each enrolment status when only one active enrolment is found?";
    private const string Cq004 = "Should documents be delivered to Careway through the API, by email to info@careway.co.nz, or both?";
    private const string Cq005 = "How should Axis/Specialists assign the IDT physiotherapist and send their board number to Careway when the initial referral has not come from a physio?";
    private const string Cq006 = "How should invoices be batched and sent to Careway, given that the Careway service code determines the batching?";
    private const string Cq009 = "In what format should outcome tool data be sent to Careway: as fields or as a document inserted into a letter?";
    private const string Cq012 = "Should a second referral letter or the Cc field be used to add the physiotherapist to the patient's IDT?";
    private const string Cq013 = "How should the ARTP be sent to Careway in addition to the clinic letter, given that only one letter can be produced per appointment?";
    private const string Cq014 = "In the Special Case, where the funder is changed to ACC-Careway after an appointment, which provider should Careway record as enrolling the patient: the logged-in administrator or the IDT member?";
    private const string WristQuestion = "Should Wrist/hand referrals be supported by Careway, given that the bilateral rule calls the API twice for Wrist while the injury-type rule returns an error for Wrist/hand?";

    private static ClarificationCandidate Candidate(string kind, string topic, string question, params string[] evidence) =>
        new(topic, question, "Checked every section that mentions it; none settles it.", kind,
            "Decides how the integration is implemented and tested.", evidence);

    private static ClarificationCandidateValidator.Context Context(string srs, params ExistingClarification[] existing) =>
        new(Guid.NewGuid(), srs, existing);

    private CandidateVerdict Check(ClarificationCandidateValidator.Context context, ClarificationCandidate candidate)
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(context, candidate);
        Report(verdict);
        return verdict;
    }

    private void Report(CandidateVerdict verdict)
    {
        output.WriteLine($"{verdict.Outcome.ToUpperInvariant()} [{verdict.Kind}] {verdict.Candidate.Question}");
        foreach (var e in verdict.Evidence) output.WriteLine($"    line {e.Line + 1} [{e.Heading}] {e.Excerpt}");
        foreach (var r in verdict.Rejections) output.WriteLine($"    x {r.Code}: {r.Message}");
    }

    private static HashSet<int> EvidenceLines(CandidateVerdict verdict) => verdict.Evidence.Select(e => e.Line).ToHashSet();

    // ==== Sections: every passage is labelled with the section it is really in ===========================

    [Fact]
    public async Task Careway_sections_come_from_the_table_of_contents_not_from_list_items()
    {
        var srs = await Careway();
        var index = SourceReferenceIndex.Build(Guid.NewGuid(), srs);
        string? HeadingOf(string fragment) => index.SectionOf(LineOf(srs, fragment))?.HeadingLine;

        Assert.Equal("Background", HeadingOf(EmailNotification));
        Assert.Equal("Referral Form", HeadingOf(PriorityField));
        Assert.StartsWith("Process Flow A:", HeadingOf(DocTypesVsStatus));
        Assert.StartsWith("Process Flow A:", HeadingOf(ApiDelivery));
        Assert.StartsWith("Process Flow A:", HeadingOf(ServiceCodes));
        Assert.StartsWith("Process Flow D:", HeadingOf(ArtpConsider));
        Assert.Equal("Special Case", HeadingOf(SpecialCaseEnroller));
        Assert.Equal("Consult Screen", HeadingOf(EmailCopies));

        // The list items and lead-ins that used to be taken for headings are text inside sections.
        var bodyHeadings = index.Sections.Where(s => !s.TableOfContents).Select(s => s.HeadingLine).ToList();
        output.WriteLine(string.Join(Environment.NewLine, bodyHeadings));
        Assert.DoesNotContain(bodyHeadings, h => h.Contains("Does Axis want a confirmation and error email?"));
        Assert.DoesNotContain(bodyHeadings, h => h.Contains("As ‘ACC-Careway selected on the Referral form:"));
        Assert.DoesNotContain("Fields:", bodyHeadings);
        Assert.Equal(11, index.Sections.Count(s => s.TableOfContents));
    }

    [Fact]
    public void Without_a_table_of_contents_headings_are_found_as_before()
    {
        const string srs = """
            # Leave Management

            ## 2. Approval
            Fields:
            Approver: line manager
            """;
        var index = SourceReferenceIndex.Build(Guid.NewGuid(), srs);
        Assert.Contains(index.Sections, s => s.HeadingLine == "Fields:");
        Assert.Contains(index.Sections, s => s.HeadingLine == "## 2. Approval");
    }

    [Fact]
    public void A_table_of_contents_the_body_does_not_follow_changes_nothing()
    {
        // The entries are not written again in the body: the body's own headings are kept.
        const string srs = """
            Contents
            Overview	1
            Scope	2
            Glossary	3

            1. Introduction
            Users submit leave requests.
            Notes:
            The portal is available all day.
            """;
        var index = SourceReferenceIndex.Build(Guid.NewGuid(), srs);
        Assert.Contains(index.Sections, s => !s.TableOfContents && s.HeadingLine == "Notes:");
        Assert.Contains(index.Sections, s => !s.TableOfContents && s.HeadingLine == "1. Introduction");
    }

    // ==== The Careway regressions named in the requirement ==============================================

    [Fact]
    public async Task R1_the_Wrist_hand_contradiction_is_identified_with_both_conflicting_passages()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("contradiction", "Wrist/hand support", WristQuestion, BilateralWrist, WristUnsupported));

        Assert.True(verdict.Accepted);
        Assert.Equal(ClarificationOutcomes.Valid, verdict.Outcome);
        Assert.Equal([LineOf(srs, BilateralWrist), LineOf(srs, WristUnsupported)], verdict.Evidence.Select(e => e.Line).ToArray());
        Assert.NotNull(verdict.Relation!.Conflict);
        Assert.Null(verdict.Relation.Resolution);
        Assert.Contains("Wrist", verdict.Relation.SharedSubject);
        Assert.StartsWith("Process Flow A:", verdict.Evidence[0].Heading);
        Assert.Equal("Referral Form", verdict.Evidence[1].Heading);
    }

    [Fact]
    public async Task R1_a_Wrist_question_that_assumes_one_rule_wins_is_held_for_review_not_lost()
    {
        var srs = await Careway();
        var context = Context(srs);
        var verdicts = ClarificationCandidateValidator.Validate(context,
        [
            Candidate("contradiction", "Bilateral wrist calls", "How many API calls should be made for a bilateral Wrist referral?", BilateralWrist, WristUnsupported)
        ]);
        Report(verdicts[0]);

        Assert.False(verdicts[0].Accepted);
        Assert.All(verdicts[0].Rejections, r => Assert.Equal("unsupported-assumption", r.Code));
        var held = Assert.Single(ClarificationCandidateValidator.HeldForReview(context, verdicts));
        output.WriteLine(held.Note);
        Assert.Contains(held.Evidence, e => e.Line == LineOf(srs, BilateralWrist));
        Assert.Contains(held.Evidence, e => e.Line == LineOf(srs, WristUnsupported));
    }

    [Fact]
    public async Task R2_CQ005_and_CQ012_are_one_decision_and_the_kept_question_takes_the_option_evidence()
    {
        var srs = await Careway();
        var verdicts = ClarificationCandidateValidator.Validate(Context(srs),
        [
            Candidate("missing", "IDT physiotherapist assignment", Cq005, IdtConsideration),
            Candidate("ambiguous", "Second letter or Cc field", Cq012, TwoLetters, CcOption)
        ]);
        verdicts.ForEach(Report);

        Assert.True(verdicts[0].Accepted);
        Assert.Equal(ClarificationOutcomes.Duplicate, verdicts[1].Outcome);
        var duplicate = Assert.Single(verdicts[1].Rejections);
        Assert.Equal("duplicate", duplicate.Code);
        Assert.Contains("were added to that question", duplicate.Message);

        // CQ-005 now shows the passage that raises the decision and the Cc option that answers it.
        Assert.Contains(LineOf(srs, IdtConsideration), EvidenceLines(verdicts[0]));
        Assert.Contains(LineOf(srs, CcOption), EvidenceLines(verdicts[0]));
        Assert.All(verdicts[0].Evidence, e => Assert.StartsWith("Process Flow D:", e.Heading));
    }

    [Fact]
    public async Task R2_the_duplicate_is_found_whichever_question_comes_first()
    {
        var srs = await Careway();
        var verdicts = ClarificationCandidateValidator.Validate(Context(srs),
        [
            Candidate("ambiguous", "Second letter or Cc field", Cq012, TwoLetters, CcOption),
            Candidate("missing", "IDT physiotherapist assignment", Cq005, IdtConsideration)
        ]);
        verdicts.ForEach(Report);

        Assert.True(verdicts[0].Accepted);
        Assert.Contains(verdicts[1].Rejections, r => r.Code == "duplicate");
    }

    [Fact]
    public async Task R3_CQ003_gets_document_type_and_enrolment_status_evidence_not_the_email_notification()
    {
        var srs = await Careway();

        var wrong = Check(Context(srs), Candidate("undefined-rule", "Document rules", Cq003, EmailNotification));
        Assert.False(wrong.Accepted);
        Assert.Equal(ClarificationOutcomes.Unsupported, wrong.Outcome);

        var verdict = Check(Context(srs), Candidate("undefined-rule", "Document rules", Cq003, DocTypesVsStatus, DocumentCategory));
        Assert.True(verdict.Accepted);
        Assert.Equal([LineOf(srs, DocTypesVsStatus), LineOf(srs, DocumentCategory)], verdict.Evidence.Select(e => e.Line).ToArray());
        Assert.All(verdict.Evidence, e => Assert.StartsWith("Process Flow A:", e.Heading));
        Assert.All(verdict.Evidence, e => Assert.DoesNotContain("email", e.Heading!, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(verdict.Evidence, e => e.Line == LineOf(srs, EmailNotification));
    }

    [Fact]
    public async Task R4_CQ004_gets_evidence_about_the_document_delivery_mechanisms()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("ambiguous", "Document delivery", Cq004, ApiDelivery, EmailCopies));

        Assert.True(verdict.Accepted);
        Assert.Equal([LineOf(srs, ApiDelivery), LineOf(srs, EmailCopies)], verdict.Evidence.Select(e => e.Line).ToArray());
        Assert.StartsWith("Process Flow A:", verdict.Evidence[0].Heading);
        Assert.Equal("Consult Screen", verdict.Evidence[1].Heading);

        // A passage that only shares the system's name is not delivery evidence.
        var keywordOnly = Check(Context(srs), Candidate("ambiguous", "Document delivery", Cq004, PriorityField));
        Assert.Contains(keywordOnly.Rejections, r => r.Code == "irrelevant-evidence");
    }

    [Fact]
    public async Task R5_CQ006_gets_evidence_about_invoice_batching_and_service_codes()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("missing", "Invoice batching", Cq006, ServiceCodes, InvoiceBatch));

        Assert.True(verdict.Accepted);
        Assert.Equal([LineOf(srs, ServiceCodes), LineOf(srs, InvoiceBatch)], verdict.Evidence.Select(e => e.Line).ToArray());

        // The original question with a passage that shares only "Careway" has no evidence.
        var original = Check(Context(srs), Candidate("missing", "Invoice timing",
            "What should be the frequency and timing for submitting invoices to Careway?", PriorityField));
        Assert.False(original.Accepted);
        Assert.Contains(original.Rejections, r => r.Code == "irrelevant-evidence");
        Assert.Equal(ClarificationOutcomes.Unsupported, original.Outcome);
    }

    [Fact]
    public async Task R6_CQ009_gets_evidence_about_outcome_tool_data_formats_and_an_unrelated_passage_is_not_attached()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("ambiguous", "Outcome tool data format", Cq009, OutcomeExtraction, CheckboxLocation, OutcomeFormat));

        Assert.True(verdict.Accepted);
        Assert.Equal([LineOf(srs, OutcomeExtraction), LineOf(srs, OutcomeFormat)], verdict.Evidence.Select(e => e.Line).ToArray());
        Assert.DoesNotContain(verdict.Evidence, e => e.Line == LineOf(srs, CheckboxLocation));
        var sourceCheck = Assert.Single(verdict.Assessment!, c => c.Criterion == "source-evidence");
        Assert.Contains($"line {LineOf(srs, CheckboxLocation) + 1}", sourceCheck.Detail);
    }

    [Fact]
    public async Task R7_CQ013_gets_evidence_about_ARTP_delivery()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("missing", "ARTP delivery", Cq013, ArtpConsider, ArtpEmail));

        Assert.True(verdict.Accepted);
        Assert.Equal([LineOf(srs, ArtpConsider), LineOf(srs, ArtpEmail)], verdict.Evidence.Select(e => e.Line).ToArray());

        // A referral-letter passage is not about the ARTP; and the open note is not taken as an answer.
        var wrong = Check(Context(srs), Candidate("missing", "ARTP delivery", Cq013, ReferralLetterToCareway));
        Assert.Contains(wrong.Rejections, r => r.Code == "evidence-misses-subject" && r.Message.Contains("ARTP"));
        Assert.DoesNotContain(wrong.Rejections, r => r.Code == "answered-in-source");
    }

    [Fact]
    public async Task R8_CQ014_gets_evidence_about_the_Special_Case_and_the_enrolling_provider()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("ambiguous", "Special Case enrolling provider", Cq014, SpecialCaseEnroller, SpecialCaseFunder));

        Assert.True(verdict.Accepted);
        Assert.Equal([LineOf(srs, SpecialCaseEnroller), LineOf(srs, SpecialCaseFunder)], verdict.Evidence.Select(e => e.Line).ToArray());
        Assert.All(verdict.Evidence, e => Assert.Equal("Special Case", e.Heading));
        Assert.DoesNotContain(verdict.Rejections, r => r.Code == "resolved-in-source"); // "become an exception ..." settles nothing

        var wrong = Check(Context(srs), Candidate("ambiguous", "Special Case enrolling provider", Cq014, DraftEnrolment));
        Assert.Contains(wrong.Rejections, r => r.Code == "evidence-misses-subject");
    }

    // ==== Valid questions ===============================================================================

    [Fact]
    public async Task Valid_questions_of_every_kind_are_accepted_with_their_own_passages()
    {
        var srs = await Careway();
        var verdicts = ClarificationCandidateValidator.Validate(Context(srs),
        [
            Candidate("undefined-rule", "Document rules", Cq003, DocTypesVsStatus, DocumentCategory),   // undefined rule
            Candidate("ambiguous", "Document delivery", Cq004, ApiDelivery, EmailCopies),                // ambiguity, two passages
            Candidate("missing", "Invoice batching", Cq006, ServiceCodes, InvoiceBatch),                // missing information
            Candidate("contradiction", "Wrist/hand support", WristQuestion, BilateralWrist, WristUnsupported),
            Candidate("missing", "ARTP delivery", Cq013, ArtpConsider, ArtpEmail),
            Candidate("ambiguous", "Special Case enrolling provider", Cq014, SpecialCaseEnroller, SpecialCaseFunder)
        ]);
        verdicts.ForEach(Report);

        Assert.All(verdicts, v => Assert.True(v.Accepted, v.Candidate.Question));

        // No passage is shared between questions: each question has its own evidence.
        var all = verdicts.SelectMany(v => v.Evidence.Select(e => e.Line)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    // ==== Invalid questions =============================================================================

    [Fact]
    public async Task A_question_the_source_answers_elsewhere_is_not_asked()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("missing", "Physio pathway", "Which pathway applies when a referral is entered by a physio?",
            "Pathway options are surgical and non-surgical."));

        var answered = Assert.Single(verdict.Rejections, r => r.Code == "answered-in-source");
        Assert.Contains("If referral is being entered by a physio, then it is a non-surgical pathway.", answered.Message);
        Assert.Equal(ClarificationOutcomes.AlreadyAnswered, verdict.Outcome);
    }

    [Fact]
    public async Task An_open_consideration_in_the_source_is_never_taken_as_the_answer()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("missing", "ARTP delivery", Cq013, ArtpEmail));

        Assert.True(verdict.Accepted); // "Consider how to send ARTP ..." asks the same thing; it does not answer it
    }

    [Fact]
    public async Task A_question_built_on_a_keyword_match_alone_is_rejected()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("missing", "Careway referrals", "What should happen to Careway referrals?", PriorityField));

        Assert.False(verdict.Accepted);
        Assert.Equal(ClarificationOutcomes.Unsupported, verdict.Outcome);
    }

    [Fact]
    public async Task A_question_with_fabricated_evidence_is_rejected()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("missing", "Invoice frequency", "How often should invoices be sent to Careway?",
            "Invoices must be submitted to Careway every Friday by 5pm."));

        Assert.Contains(verdict.Rejections, r => r.Code == "no-evidence");
        Assert.Empty(verdict.Evidence);
    }

    [Fact]
    public async Task A_question_whose_evidence_is_about_something_else_is_rejected()
    {
        var srs = await Careway();
        var verdict = Check(Context(srs), Candidate("undefined-rule", "Document rules", Cq003, EmailNotification));

        Assert.Contains(verdict.Rejections, r => r.Code is "unrelated" or "irrelevant-evidence");
    }

    [Fact]
    public async Task Repeated_wording_is_never_placed_at_a_guessed_occurrence()
    {
        var srs = await Careway();
        Assert.True(LineOf(srs, ConsentUrl, 2) > LineOf(srs, ConsentUrl, 1)); // written three times

        var verdict = Check(Context(srs), Candidate("missing", "Consent", "Which consent wording should the Careway consent URL/QR code message use?", ConsentUrl));
        Assert.Contains(verdict.Rejections, r => r.Code == "ambiguous-location");
    }

    // ==== Duplicates ====================================================================================

    [Fact]
    public async Task Exact_and_reworded_duplicates_are_one_question()
    {
        var srs = await Careway();
        var verdicts = ClarificationCandidateValidator.Validate(Context(srs),
        [
            Candidate("missing", "ARTP delivery", Cq013, ArtpConsider),
            Candidate("missing", "ARTP delivery", Cq013, ArtpConsider),
            Candidate("missing", "ARTP sending", "How should the ARTP be sent to Careway in addition to the clinic letter?", ArtpConsider, ArtpEmail)
        ]);
        verdicts.ForEach(Report);

        Assert.True(verdicts[0].Accepted);
        Assert.Contains(verdicts[1].Rejections, r => r.Code == "duplicate");
        Assert.Contains(verdicts[2].Rejections, r => r.Code == "duplicate");
        Assert.Contains(LineOf(srs, ArtpEmail), EvidenceLines(verdicts[0])); // the rewording's extra passage is kept
    }

    [Fact]
    public void Synonyms_make_one_question()
    {
        const string srs = """
            # Expenses

            ## 2. Claims
            Expense claims are routed for approval after submission.
            """;
        var verdicts = ClarificationCandidateValidator.Validate(Context(srs),
        [
            Candidate("missing", "Claim approver", "Who approves expense claims?", "Expense claims are routed for approval after submission."),
            Candidate("missing", "Claim authoriser", "Who authorises expense claims?", "Expense claims are routed for approval after submission.")
        ]);
        verdicts.ForEach(Report);

        Assert.True(verdicts[0].Accepted);
        Assert.Contains(verdicts[1].Rejections, r => r.Code == "duplicate");
    }

    [Fact]
    public async Task Similar_questions_that_need_different_decisions_are_both_kept()
    {
        var srs = await Careway();
        var verdicts = ClarificationCandidateValidator.Validate(Context(srs),
        [
            Candidate("missing", "IDT physiotherapist assignment", Cq005, IdtConsideration),
            // Both about letters and providers, but a different decision each.
            Candidate("missing", "ARTP delivery", Cq013, ArtpConsider, ArtpEmail),
            Candidate("missing", "Referral Admin NZMC number", "Which dummy NZMC number should be used for the Referral Admin user?", NzmcAdmin)
        ]);
        verdicts.ForEach(Report);

        Assert.All(verdicts, v => Assert.True(v.Accepted, v.Candidate.Question));
    }

    // ==== Contradictions ================================================================================

    [Fact]
    public void A_genuine_contradiction_across_sections_is_kept_and_asked_neutrally()
    {
        const string srs = """
            # Bookings

            ## 3. Cancellation
            Patients may cancel a booking up to 24 hours before the appointment.

            ## 7. Fees
            Bookings cannot be cancelled less than 48 hours before the appointment.
            """;
        var verdict = Check(Context(srs), Candidate("contradiction", "Cancellation window",
            "Is the cancellation cut-off 24 hours or 48 hours before the appointment?",
            "Patients may cancel a booking up to 24 hours before the appointment.",
            "Bookings cannot be cancelled less than 48 hours before the appointment."));

        Assert.True(verdict.Accepted);
        Assert.Equal(2, verdict.Evidence.Count);
    }

    [Fact]
    public void A_general_rule_and_its_stated_exception_are_not_a_contradiction()
    {
        const string srs = """
            # Referrals

            ## 2. Routing
            All referrals are sent to Careway.

            ## 5. Private patients
            As an exception to the routing rule, privately funded referrals are not sent to Careway.
            """;
        var verdict = Check(Context(srs), Candidate("contradiction", "Routing",
            "Are privately funded referrals sent to Careway?",
            "All referrals are sent to Careway.",
            "privately funded referrals are not sent to Careway."));

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Rejections, r => r.Code is "different-conditions" or "resolved-in-source");
    }

    [Fact]
    public void Rules_in_different_workflows_that_look_contradictory_are_not_a_contradiction()
    {
        const string srs = """
            # Referral form

            ## New referrals
            Body Side is mandatory.

            ## Amendments
            Body Side is optional.
            """;
        var verdict = Check(Context(srs), Candidate("contradiction", "Body Side",
            "Is Body Side mandatory or optional?", "Body Side is mandatory.", "Body Side is optional."));

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Rejections, r => r.Code == "different-conditions");
    }

    [Fact]
    public void Exception_meaning_an_error_case_does_not_settle_two_rules()
    {
        const string srs = """
            # Leave

            ## 3. Cancellation
            Employees may cancel a request up to 24 hours before the leave starts.

            ## 6. Policy
            Requests can be cancelled up to 48 hours before the start date.

            ## 9. Errors
            A request that cannot be cancelled is raised as an exception and shown in the exception menu.
            """;
        var verdict = Check(Context(srs), Candidate("contradiction", "Cancellation deadline",
            "Is the cancellation deadline 24 hours or 48 hours before the leave starts?",
            "Employees may cancel a request up to 24 hours before the leave starts",
            "Requests can be cancelled up to 48 hours before the start date"));

        Assert.True(verdict.Accepted);
        Assert.Null(verdict.Relation!.Resolution);
    }

    // ==== Document scenarios ============================================================================

    [Fact]
    public async Task A_PDF_question_is_located_on_the_page_its_passage_is_on()
    {
        var pdf = PdfFixtureBuilder.Text(
        [
            ["Careway Invoicing", "Background", "Axis and USP share the PMS Indici for patient management across Auckland clinics."],
            ["Invoicing", "CCR team will create a new contract with Careway service codes.", ServiceCodes, "Physio selects Careway contract codes appropriate for the consultation."],
            ["Payments", InvoiceBatch + "."]
        ]);
        var extracted = await new DocumentTextExtractor().ExtractDocumentAsync(pdf, "careway-invoicing.pdf");
        var verdict = Check(Context(extracted.Text), Candidate("missing", "Invoice batching", Cq006, ServiceCodes, InvoiceBatch));

        Assert.True(verdict.Accepted);
        var starts = extracted.PageLineStarts!;
        int PageOf(int line) => starts.Count(s => s <= line);
        Assert.Equal([2, 3], verdict.Evidence.Select(e => PageOf(e.Line)).ToArray());
    }

    [Fact]
    public void A_table_row_that_answers_the_question_is_found()
    {
        var srs = """
            # Booking requirements

            ## 3. Reminders
            Patients are sent a reminder before each appointment.

            | Reminder | Channel | Timing |
            | --- | --- | --- |
            | Appointment reminder | SMS | Sent 24 hours before the appointment |
            | Follow-up survey | Email | 2 days after the appointment |
            """;
        var verdict = Check(Context(srs), Candidate("missing", "Reminder timing", "When is the appointment reminder sent?",
            "Patients are sent a reminder before each appointment."));

        Assert.Contains(verdict.Rejections, r => r.Code == "answered-in-source");
    }

    // ==== Reading stored questions: labels and passages corrected without writing anything ==============

    [Fact]
    public async Task A_stored_question_is_shown_with_the_section_its_passage_is_really_in()
    {
        var srs = await Careway();
        var sourceId = Guid.NewGuid();
        var line = LineOf(srs, DocTypesVsStatus);
        var stored = new ClarificationQuestionPayload
        {
            Question = Cq003, Kind = "undefined-rule", Reason = "Not defined.", ClarificationStatus = "Open",
            SourceEvidence =
            [
                new SourceReferencePayload
                {
                    Label = DocTypesVsStatus, Excerpt = DocTypesVsStatus, ExcerptIsQuote = true, SourceId = sourceId, Line = line, EndLine = line + 1,
                    Anchor = $"L{line}", Heading = "2.\tDoes Axis want a confirmation and error email?"   // the old, wrong label
                }
            ]
        };

        var linked = JsonSerializer.Deserialize<ClarificationQuestionPayload>(
            ClarificationSourceReferences.WithSourceLinks(JsonSerializer.Serialize(stored), sourceId, ClarificationEvidenceLocator.ForSource(sourceId, srs)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var passage = Assert.Single(linked.SourceEvidence!);
        Assert.Equal(line, passage.Line);
        Assert.StartsWith("Process Flow A:", passage.Heading);
    }

    [Fact]
    public async Task A_stored_question_does_not_show_an_unrelated_passage_beside_its_evidence()
    {
        var srs = await Careway();
        var sourceId = Guid.NewGuid();
        var locator = ClarificationEvidenceLocator.ForSource(sourceId, srs);
        var evidence = locator.Locate([OutcomeExtraction, CheckboxLocation]);
        Assert.Equal(2, evidence.Count);
        var stored = new ClarificationQuestionPayload
        {
            Question = Cq009, Kind = "ambiguous", Reason = "Not defined.", ClarificationStatus = "Open", SourceEvidence = evidence
        };

        var linked = JsonSerializer.Deserialize<ClarificationQuestionPayload>(
            ClarificationSourceReferences.WithSourceLinks(JsonSerializer.Serialize(stored), sourceId, locator),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var shown = Assert.Single(linked.SourceEvidence!);
        Assert.Equal(LineOf(srs, OutcomeExtraction), shown.Line);
        Assert.True(ClarificationSourceReferences.IsGrounded(JsonSerializer.Serialize(linked)));
    }

    [Fact]
    public async Task Recovery_never_borrows_another_questions_passages_through_a_shared_reason()
    {
        var srs = await Careway();
        var sourceId = Guid.NewGuid();
        const string sharedReason = "The SRS does not specify this.";
        var stored = new ClarificationQuestionPayload { Question = "A question reworded by a reviewer?", Kind = "missing", Reason = sharedReason, ClarificationStatus = "Open" };
        var trace = new ClarificationSourceReferences.GenerationTrace(
        [
            new GeneratedClarification(Cq013, sharedReason, [ArtpConsider], []),
            new GeneratedClarification(Cq006, sharedReason, [ServiceCodes], [])
        ], OriginalQuestion: null);

        var linked = JsonSerializer.Deserialize<ClarificationQuestionPayload>(
            ClarificationSourceReferences.WithSourceLinks(JsonSerializer.Serialize(stored), sourceId, ClarificationEvidenceLocator.ForSource(sourceId, srs), trace),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.DoesNotContain(linked.SourceEvidence ?? [], e => e.Line >= 0);
        Assert.False(ClarificationSourceReferences.IsGrounded(JsonSerializer.Serialize(linked)));
    }

    // ==== End to end: the analysis service over the Careway SRS =========================================

    [Fact]
    public async Task Careway_analysis_saves_only_valid_questions_each_with_its_own_correctly_located_evidence()
    {
        var srs = await Careway();
        var (db, source) = Seed(srs);
        var ai = new ScriptedAiChatClient(Response(
            // As originally generated: wrong or keyword-only evidence.
            Question("undefined-rule", "Document rules", "What rules should apply to each document type for each enrolment status?", EmailNotification),
            Question("missing", "Invoice timing", "What should be the frequency and timing for submitting invoices to Careway?", PriorityField),
            Question("missing", "ARTP delivery", "How should the ARTP be sent to Careway in addition to the clinic letter?", ReferralLetterToCareway),
            Question("contradiction", "Bilateral wrist calls", "How many API calls should be made for a bilateral Wrist referral?", BilateralWrist, WristUnsupported),
            // Corrected.
            Question("undefined-rule", "Document rules per enrolment status", Cq003, DocTypesVsStatus, DocumentCategory),
            Question("ambiguous", "Document delivery", Cq004, ApiDelivery, EmailCopies),
            Question("missing", "IDT physiotherapist assignment", Cq005, IdtConsideration),
            Question("missing", "Invoice batching", Cq006, ServiceCodes, InvoiceBatch),
            Question("ambiguous", "Outcome tool data format", Cq009, OutcomeExtraction, CheckboxLocation, OutcomeFormat),
            Question("ambiguous", "Second letter or Cc field", Cq012, TwoLetters, CcOption),
            Question("missing", "ARTP delivery to Careway", Cq013, ArtpConsider, ArtpEmail),
            Question("ambiguous", "Special Case enrolling provider", Cq014, SpecialCaseEnroller, SpecialCaseFunder),
            Question("contradiction", "Wrist/hand support", WristQuestion, BilateralWrist, WristUnsupported)));

        var result = await new RequirementAnalysisService(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(),
            new BatchModeOff(), NullLogger<RequirementAnalysisService>.Instance).AnalyzeAsync(source.Id);

        foreach (var r in result.RejectedCandidates ?? [])
        {
            output.WriteLine($"{r.Outcome}: \"{r.Question}\" -> {string.Join("; ", r.Reasons.Select(x => x.Code))}");
        }

        var saved = InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion)
            .Select(a => JsonSerializer.Deserialize<ClarificationQuestionPayload>(a.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
            .ToDictionary(p => p.Question);
        foreach (var (question, payload) in saved)
        {
            output.WriteLine($"SAVED {question}");
            foreach (var e in payload.SourceEvidence!) output.WriteLine($"    line {e.Line + 1} [{e.Heading}] {e.Excerpt}");
        }

        Assert.Equal(new[] { Cq003, Cq004, Cq005, Cq006, Cq009, Cq013, Cq014, WristQuestion }.Order(), saved.Keys.Order());
        int[] Lines(string question) => saved[question].SourceEvidence!.Select(e => e.Line).ToArray();
        Assert.Equal([LineOf(srs, DocTypesVsStatus), LineOf(srs, DocumentCategory)], Lines(Cq003));
        Assert.Equal([LineOf(srs, ApiDelivery), LineOf(srs, EmailCopies)], Lines(Cq004));
        Assert.Equal(LineOf(srs, IdtConsideration), Lines(Cq005)[0]);                              // its own passage first,
        Assert.Contains(LineOf(srs, CcOption), Lines(Cq005));                                       // then the options merged from CQ-012
        Assert.All(Lines(Cq005), l => Assert.InRange(l, LineOf(srs, IdtConsideration), LineOf(srs, CcOption)));
        Assert.Equal([LineOf(srs, ServiceCodes), LineOf(srs, InvoiceBatch)], Lines(Cq006));
        Assert.Equal([LineOf(srs, OutcomeExtraction), LineOf(srs, OutcomeFormat)], Lines(Cq009));
        Assert.Equal([LineOf(srs, ArtpConsider), LineOf(srs, ArtpEmail)], Lines(Cq013));
        Assert.Equal([LineOf(srs, SpecialCaseEnroller), LineOf(srs, SpecialCaseFunder)], Lines(Cq014));
        Assert.Equal([LineOf(srs, BilateralWrist), LineOf(srs, WristUnsupported)], Lines(WristQuestion));
        Assert.All(saved.Values.SelectMany(p => p.SourceEvidence!), e => Assert.DoesNotContain("Does Axis want", e.Heading ?? string.Empty));

        // Every rejected candidate carries its validation result. The one-sided wrist question is not held:
        // the neutral Wrist/hand question saved above already sets the same two passages against each other.
        var rejected = result.RejectedCandidates!;
        Assert.All(rejected, r => Assert.NotNull(r.Outcome));
        Assert.Equal(ClarificationOutcomes.Duplicate, Assert.Single(rejected, r => r.Question == Cq012).Outcome);
        Assert.Equal(ClarificationOutcomes.Unsupported, Assert.Single(rejected, r => r.Question.StartsWith("What should be the frequency")).Outcome);
        Assert.Equal(ClarificationOutcomes.Unsupported, Assert.Single(rejected, r => r.Question.StartsWith("How many API calls")).Outcome);
        Assert.Null(result.HeldForReview);
    }

    private static (InMemoryAiReapDb Db, RequirementSource Source) Seed(string rawText)
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "Careway accuracy", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = rawText,
            CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        return (db, source);
    }

    private static JsonObject Question(string kind, string topic, string question, params string[] evidence) => new()
    {
        ["kind"] = kind, ["topic"] = topic, ["question"] = question,
        ["reason"] = "Checked every section that mentions it; none settles it.",
        ["impact"] = "Decides how the integration is implemented and tested.",
        ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e).ToArray())
    };

    private static string Response(params JsonObject[] questions) => new JsonObject
    {
        ["actors"] = new JsonArray("Axis", "Careway", "Physiotherapist"),
        ["capabilities"] = new JsonArray("Send referral to Careway", "Send documentation to Careway", "Batch invoices"),
        ["dataElements"] = new JsonArray("Referral", "Enrolment", "Invoice"),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(questions.Cast<JsonNode>().ToArray())
    }.ToJsonString();
}
