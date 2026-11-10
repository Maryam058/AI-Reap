using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Knowledge;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// ClarificationCandidateValidator against questions saved before RequirementAnalysis-v3 (no kind, no
// evidence) and against answers reached through cross-references ("See Appendix B", "section 4.2").
// Offline: the validator is deterministic; the one service test uses ScriptedAiChatClient over
// InMemoryAiReapDb. No SQL Server, no network, no provider.
public class ClarificationLegacyAndCrossReferenceTests(ITestOutputHelper output)
{
    private const string LeaveSrs = """
        # Leave Management

        ## 1. Submitting
        Employees submit leave requests through the portal.

        ## 2. Approval
        Each leave request is routed for approval.
        """;

    private const string RoutedForApproval = "Each leave request is routed for approval.";

    private static CandidateVerdict Check(string source, ClarificationCandidate candidate, params ExistingClarification[] existing)
    {
        var context = new ClarificationCandidateValidator.Context(Guid.NewGuid(), source, existing);
        return ClarificationCandidateValidator.ValidateOne(context, candidate);
    }

    private static ClarificationCandidate Candidate(string question, params string[] evidence) =>
        new("Approval", question, "The source does not settle this.", "missing", "Decides who acts on a request and how it is tested.", evidence);

    // A question as saved before v3: no kind, no stored evidence.
    private static ExistingClarification Legacy(
        string code, string question, string status = "Open", string? topic = null, string? reason = null, string? answer = null,
        List<SourceReferencePayload>? references = null) =>
        new(code, question, topic, status, new HashSet<int>(), null, reason, answer, references);

    private void Report(CandidateVerdict verdict) =>
        output.WriteLine($"{(verdict.Accepted ? "ACCEPTED" : "REJECTED")} \"{verdict.Candidate.Question}\" " +
            string.Join("; ", verdict.Rejections.Select(r => $"{r.Code}: {r.Message}")));

    // ---- P1-A: duplicates of questions saved before v3 ------------------------------------------------

    [Fact]
    public void A_reworded_question_duplicates_an_open_legacy_question_with_no_kind_or_evidence()
    {
        var verdict = Check(LeaveSrs, Candidate("Who must approve a leave request?", RoutedForApproval),
            Legacy("CQ-001", "Who authorises leave requests?"));
        Report(verdict);

        var duplicate = Assert.Single(verdict.Rejections);
        Assert.Equal("duplicate", duplicate.Code);
        Assert.Equal("CQ-001", duplicate.RelatedCode);
    }

    [Fact]
    public void A_reworded_question_is_already_answered_when_the_legacy_question_was_answered()
    {
        var verdict = Check(LeaveSrs, Candidate("Who must approve a leave request?", RoutedForApproval),
            Legacy("CQ-001", "Who authorises leave requests?", "Answered", answer: "Line managers."));
        Report(verdict);

        Assert.Equal("already-answered", Assert.Single(verdict.Rejections).Code);
    }

    // Wording that shares little with the candidate, but its saved answer covers the question.
    [Fact]
    public void A_legacy_answer_that_covers_the_question_makes_it_already_answered()
    {
        var verdict = Check(LeaveSrs, Candidate("Who must approve a leave request?", RoutedForApproval),
            Legacy("CQ-007", "Who handles leave?", "Answered", topic: "Leave", answer: "Line managers approve all leave requests."));
        Report(verdict);

        var rejection = Assert.Single(verdict.Rejections);
        Assert.Equal("already-answered", rejection.Code);
        Assert.Equal("CQ-007", rejection.RelatedCode);
    }

    // A broader legacy question that holds the candidate's terms is the same question when nothing says
    // where it points (missing metadata is not a difference), or when its reason points at the same
    // section - and not when its stored links put it in another section.
    [Theory]
    [InlineData(null, false, true)]
    [InlineData("Section 2 does not say who signs off.", false, true)]
    [InlineData(null, true, false)]
    public void A_broader_legacy_question_is_matched_unless_it_is_located_in_another_section(string? reason, bool linkedToSection1, bool duplicate)
    {
        List<SourceReferencePayload>? links = null;
        if (linkedToSection1)
        {
            var section1 = SourceReferenceIndex.Build(Guid.Empty, LeaveSrs).Sections.Single(s => s.Number == "1");
            links = [new SourceReferencePayload { Label = "Section 1", Line = section1.Line, EndLine = section1.EndLine }];
        }

        var verdict = Check(LeaveSrs, Candidate("Who must approve a leave request?", RoutedForApproval),
            Legacy("CQ-003", "Who must sign off and approve a leave request once it is submitted?", reason: reason, references: links));
        Report(verdict);

        Assert.Equal(duplicate, verdict.Rejections.Any(r => r.Code == "duplicate" && r.RelatedCode == "CQ-003"));
    }

    // Genuinely different questions about the same words are not merged.
    [Theory]
    [InlineData("When must a leave request be approved?")]          // when, not who
    [InlineData("Who can view a leave request?")]                    // another action
    [InlineData("How many leave requests may an employee have open at once?")]
    public void A_different_question_about_the_same_subject_is_not_a_duplicate_of_a_legacy_question(string question)
    {
        var verdict = Check(LeaveSrs, Candidate(question, RoutedForApproval),
            Legacy("CQ-001", "Who authorises leave requests?", "Answered", answer: "Line managers."),
            Legacy("CQ-002", "Who authorises leave requests?"));
        Report(verdict);

        Assert.True(verdict.Accepted);
    }

    // Through the real analysis service: a pre-v3 question in the database is compared against and left
    // exactly as it was. It is shown because the passage its reason quotes verifies (ClarificationVisibility);
    // an open legacy question that is not shown does not block a grounded rewording
    // (ClarificationGroundingTests).
    [Fact]
    public async Task Regeneration_does_not_save_a_reworded_copy_of_a_legacy_question_and_leaves_it_unchanged()
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "Legacy CQ project", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = LeaveSrs,
            CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow
        };
        var legacyJson = JsonSerializer.Serialize(new ClarificationQuestionPayload
        {
            Question = "Who authorises leave requests?", Reason = $"\"{RoutedForApproval}\" does not name the approver.", ClarificationStatus = "Open"
        });
        var legacy = new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, RequirementSourceId = source.Id, ArtifactType = ArtifactType.ClarificationQuestion,
            Code = "CQ-001", Title = "Approver", DataJson = legacyJson, CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow.AddDays(-10)
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        db.Artifacts.Add(legacy);

        var ai = new ScriptedAiChatClient(new JsonObject
        {
            ["actors"] = new JsonArray("Employee"), ["capabilities"] = new JsonArray(), ["dataElements"] = new JsonArray(), ["notes"] = new JsonArray(),
            ["missingInformation"] = new JsonArray(new JsonObject
            {
                ["kind"] = "missing", ["topic"] = "Approval", ["question"] = "Who must approve a leave request?",
                ["reason"] = "No approver is named.", ["impact"] = "Decides the approval workflow.", ["evidence"] = new JsonArray(RoutedForApproval)
            })
        }.ToJsonString());
        var service = new RequirementAnalysisService(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(),
            new BatchModeOff(), NullLogger<RequirementAnalysisService>.Instance);

        var result = await service.AnalyzeAsync(source.Id, regenerate: true);

        Assert.Empty(result.ClarificationQuestions);
        var rejected = Assert.Single(result.RejectedCandidates!);
        Assert.Contains(rejected.Reasons, r => r.Code == "duplicate" && r.RelatedCode == "CQ-001");
        var saved = Assert.Single(InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion));
        Assert.Equal(legacyJson, saved.DataJson);
        Assert.Equal(1, saved.CurrentVersion);
    }

    // ---- P1-B: answers reached through cross-references -------------------------------------------------

    private const string CrossReferencedSrs = """
        # Leave Management

        ## 1. Submitting
        Employees submit leave requests through the portal.

        ## 2. Approval
        FR-010: Each leave request is routed for approval as set out in Appendix B.
        FR-011: Approval rules for long absences follow section 4.2.
        FR-012: Approval deadlines are listed in Appendix C.

        ## 3. Balance
        Each employee has 25 days of annual leave per calendar year.

        ## 4. Rules
        ### 4.1 General
        Requests are processed in the order received.
        ### 4.2 Long absences
        Absences longer than ten days are approved by the department director.

        Appendix B - Approval matrix
        Leave type | Approver | Approval deadline
        Annual leave | Line manager | TBD
        Sick leave | HR officer | 2 working days
        """;

    private const string Fr010 = "FR-010: Each leave request is routed for approval as set out in Appendix B.";

    [Fact]
    public void A_question_answered_in_an_appendix_table_is_rejected_with_the_row_and_the_reference_followed()
    {
        var verdict = Check(CrossReferencedSrs, Candidate("Who must approve an annual leave request?", Fr010));
        Report(verdict);

        var answered = Assert.Single(verdict.Rejections);
        Assert.Equal("answered-in-source", answered.Code);
        Assert.Contains("Appendix B - Approval matrix", answered.Location);
        Assert.Contains("reached from \"Appendix B\" (line 7)", answered.Location);
        Assert.Contains("Approver: Line manager", answered.Message); // the row, read with its column headers
        Assert.DoesNotContain("Sick leave", answered.Message);
    }

    [Fact]
    public void A_question_answered_through_a_numbered_section_reference_is_rejected()
    {
        var verdict = Check(CrossReferencedSrs,
            Candidate("Who approves absences longer than ten days?", "FR-011: Approval rules for long absences follow section 4.2."));
        Report(verdict);

        var answered = Assert.Single(verdict.Rejections);
        Assert.Equal("answered-in-source", answered.Code);
        Assert.Contains("4.2 Long absences", answered.Location);
        Assert.Contains("reached from \"section 4.2\"", answered.Location);
        Assert.Contains("department director", answered.Message);
    }

    // The referenced section shares the question's words (in its heading and rows) but does not say who
    // approves: the question stays.
    [Fact]
    public void A_referenced_section_that_shares_keywords_but_does_not_answer_keeps_the_question()
    {
        const string source = """
            ## 2. Approval
            FR-010: Leave approval terms are explained in Appendix A.

            Appendix A - Leave approval terms
            Term | Meaning
            Leave request | A request by an employee for time off
            Approval | The decision recorded on a leave request
            """;

        var verdict = Check(source, Candidate("Who must approve a leave request?", "FR-010: Leave approval terms are explained in Appendix A."));
        Report(verdict);

        Assert.True(verdict.Accepted);
    }

    // "Appendix C" is not in the source: nothing is followed, and no other passage answers.
    [Fact]
    public void A_broken_cross_reference_adds_nothing_and_the_question_stays()
    {
        const string evidence = "FR-012: Approval deadlines are listed in Appendix C.";
        Assert.Empty(SourceReferenceIndex.Build(Guid.Empty, CrossReferencedSrs).Resolve(evidence));

        var verdict = Check(CrossReferencedSrs, Candidate("What is the approval deadline for parental leave?", evidence));
        Report(verdict);

        Assert.True(verdict.Accepted);
    }

    // Following Appendix B finds the annual leave row, but its deadline is "TBD"; the sick leave row's
    // deadline is for another leave type.
    [Fact]
    public void A_genuine_ambiguity_that_remains_after_following_the_reference_keeps_the_question()
    {
        var verdict = Check(CrossReferencedSrs, Candidate("What is the approval deadline for annual leave?", Fr010));
        Report(verdict);

        Assert.True(verdict.Accepted);
    }

    // A qualifier the table never mentions ("contractors") is not answered by the row that matches the
    // rest of the question.
    [Fact]
    public void A_row_that_matches_most_words_but_not_the_questions_own_qualifier_does_not_answer_it()
    {
        var verdict = Check(CrossReferencedSrs, Candidate("Who approves annual leave for contractors?", Fr010));
        Report(verdict);

        Assert.DoesNotContain(verdict.Rejections, r => r.Code == "answered-in-source");
    }
}
