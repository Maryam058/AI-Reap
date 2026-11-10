using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Dashboard;
using AiReap.Application.Sdlc;
using AiReap.Application.Traceability;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// Only source-grounded clarification questions are ever shown: a candidate is accepted only with a
// verified passage of the SRS it is about (ClarificationCandidateValidator), and a question saved earlier
// is shown only while its passage verifies (ClarificationVisibility) - through every read path, without
// changing or deleting the saved record and without any AI call. Offline: InMemoryAiReapDb, scripted AI.
public class ClarificationGroundingTests
{
    private const string Srs = """
        # Clinic Appointment System - Software Requirements Specification

        ## 2. Booking
        FR-001 Patients shall book appointments through the online portal.
        FR-002 The portal shall confirm a booking promptly after it is submitted.
        Bookings are stored in the appointment register for seven years.

        ## 3. Cancellation
        FR-010 Patients may cancel an appointment up to 24 hours before it starts.

        ## 4. Reminders
        FR-020 The system shall send a reminder notification to the patient before each appointment.
        Reminder records are stored in the appointment register for seven years.

        ## 5. Policies
        Appointments can be cancelled up to 48 hours before the start time.

        ## 6. Reporting
        FR-030 Reception staff shall export a daily report of appointments.
        """;

    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Lines = SourceReferenceIndex.Lines(Srs);

    private static int LineOf(string text) => Array.FindIndex(Lines, l => l.Contains(text, StringComparison.Ordinal));

    private static ClarificationCandidateValidator.Context Context() => new(Guid.NewGuid(), Srs, []);

    private static ClarificationCandidate Candidate(string kind, string question, string[] evidence, string reason = "Not stated.",
        IReadOnlyList<ClaimedSourceReference>? refs = null) =>
        new(question, question, reason, kind, "Decides how the requirement is built and tested.", evidence, refs);

    private static (InMemoryAiReapDb Db, RequirementSource Source) Seed()
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "CQ grounding", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = Srs,
            CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        return (db, source);
    }

    private static Artifact SaveQuestion(InMemoryAiReapDb db, RequirementSource source, string code, object payload)
    {
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = source.ProjectId, ArtifactType = ArtifactType.ClarificationQuestion, Code = code,
            Title = code, Status = ArtifactStatus.AiGenerated, Origin = ArtifactOrigin.Ai, RequirementSourceId = source.Id,
            DataJson = JsonSerializer.Serialize(payload), CurrentVersion = 1, CreatedByUserId = "phase0-offline-user",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.Artifacts.Add(artifact);
        return artifact;
    }

    // Evidence as generation stores it: the quoted words, at their place on the line holding `onLine`
    // (by default the line that holds the quote).
    private static SourceReferencePayload Placed(Guid sourceId, string quote, string? onLine = null)
    {
        var index = SourceReferenceIndex.Build(sourceId, Srs);
        var places = index.PhraseOccurrences(quote).Select(o => index.LocateRange(o.Start, o.End)!).ToList();
        var reference = places.Single(p => p.Line == LineOf(onLine ?? quote));
        reference.Occurrences = places.Count > 1 ? places.Count : null;
        return reference;
    }

    private static async Task<Dictionary<string, JsonElement>> ReadAsync(InMemoryAiReapDb db, RequirementSource source) =>
        (await new ClarificationService(db, new AllowAllProjectAccess(), new OfflineCurrentUser()).GetForRequirementSourceAsync(source.Id))
            .ToDictionary(q => q.Code, q => q.Data);

    private static RequirementAnalysisService Analysis(InMemoryAiReapDb db, IAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(), new BatchModeOff(),
            NullLogger<RequirementAnalysisService>.Instance);

    private static string V3Response(params JsonObject[] questions) => new JsonObject
    {
        ["actors"] = new JsonArray("Patient"),
        ["capabilities"] = new JsonArray("Book appointment"),
        ["dataElements"] = new JsonArray("Appointment"),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(questions.Cast<JsonNode>().ToArray())
    }.ToJsonString();

    private static JsonObject V3Question(string kind, string question, string[] evidence, string reason = "Not stated.") => new()
    {
        ["kind"] = kind, ["topic"] = question, ["question"] = question, ["reason"] = reason,
        ["impact"] = "The answer decides how the requirement is implemented and tested.",
        ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e).ToArray()),
        ["sourceRefs"] = new JsonArray()
    };

    // ---- generation: the validator --------------------------------------------------------------

    [Fact]
    public void A_candidate_with_a_verified_passage_it_is_about_is_accepted()
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(Context(),
            Candidate("ambiguous", "What response time counts as prompt for a booking confirmation?",
                ["The portal shall confirm a booking promptly after it is submitted."]));

        Assert.True(verdict.Accepted, string.Join("; ", verdict.Rejections.Select(r => r.Message)));
        Assert.Equal(LineOf("FR-002"), Assert.Single(verdict.Evidence).Line);
    }

    [Fact]
    public void A_candidate_without_evidence_or_with_a_fabricated_quote_or_section_is_rejected()
    {
        var context = Context();
        var none = ClarificationCandidateValidator.ValidateOne(context, Candidate("missing", "Which payment methods are accepted for bookings?", []));
        var fabricated = ClarificationCandidateValidator.ValidateOne(context, Candidate("missing", "Who approves a refund for a cancelled booking?",
            ["Refunds for cancelled bookings are approved by the practice manager."],
            reason: "Section 9.4 Refunds does not name the approver.",
            refs: [new ClaimedSourceReference { Label = "Section 9.4", Section = "9.4", Heading = "Refunds", Quote = "Refunds are approved by the manager." }]));

        Assert.Contains(none.Rejections, r => r.Code == "no-evidence");
        Assert.Contains(fabricated.Rejections, r => r.Code == "no-evidence");
        Assert.Empty(fabricated.Evidence); // the claimed section and quote are never stored as evidence
    }

    [Fact]
    public void A_missing_information_question_is_accepted_only_when_grounded_in_an_existing_requirement()
    {
        var context = Context();
        var grounded = ClarificationCandidateValidator.ValidateOne(context, Candidate("missing",
            "How long before the appointment is the reminder notification sent?",
            ["The system shall send a reminder notification to the patient before each appointment."]));
        // Only topic words ("appointment", "patient") in common with the source: nothing establishes the context.
        var ungrounded = ClarificationCandidateValidator.ValidateOne(context, Candidate("missing",
            "Which languages must the patient appointment portal support?",
            ["The portal shall support English and Welsh for every patient appointment."]));

        Assert.True(grounded.Accepted, string.Join("; ", grounded.Rejections.Select(r => r.Message)));
        Assert.Equal(LineOf("FR-020"), Assert.Single(grounded.Evidence).Line);
        Assert.False(ungrounded.Accepted);
        Assert.Contains(ungrounded.Rejections, r => r.Code == "no-evidence");
    }

    [Fact]
    public void A_question_sharing_only_a_requirement_id_with_its_passage_is_unrelated()
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(Context(), Candidate("ambiguous",
            "Is FR-030 still in scope for release 2?",
            ["FR-030 Reception staff shall export a daily report of appointments."]));

        Assert.Contains(verdict.Rejections, r => r.Code == "unrelated");
    }

    [Fact]
    public void A_conflict_is_accepted_only_when_both_statements_are_verified()
    {
        var context = Context();
        var both = ClarificationCandidateValidator.ValidateOne(context, Candidate("contradiction",
            "Is the cancellation deadline 24 hours or 48 hours before the appointment?",
            ["Patients may cancel an appointment up to 24 hours before it starts.", "Appointments can be cancelled up to 48 hours before the start time."]));
        var one = ClarificationCandidateValidator.ValidateOne(Context(), Candidate("contradiction",
            "Is the cancellation deadline 24 hours or 72 hours before the appointment?",
            ["Patients may cancel an appointment up to 24 hours before it starts.", "Appointments can be cancelled up to 72 hours before the start time."]));

        Assert.True(both.Accepted, string.Join("; ", both.Rejections.Select(r => r.Message)));
        Assert.Equal([LineOf("FR-010"), LineOf("48 hours")], both.Evidence.Select(e => e.Line));
        Assert.Contains(one.Rejections, r => r.Code == "contradiction-needs-both-statements");
    }

    [Fact]
    public void Repeated_wording_resolves_to_the_occurrence_the_generation_evidence_cites()
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(Context(), Candidate("missing",
            "Are reminder records kept after the appointment is cancelled?",
            ["are stored in the appointment register for seven years"],
            reason: "Section 4 keeps reminder records but does not cover cancellations.",
            refs: [new ClaimedSourceReference { Label = "Section 4", Section = "4" }]));

        Assert.True(verdict.Accepted, string.Join("; ", verdict.Rejections.Select(r => r.Message)));
        var evidence = Assert.Single(verdict.Evidence);
        Assert.Equal(LineOf("Reminder records are stored"), evidence.Line); // not the Booking one, which comes first
        Assert.Equal(2, evidence.Occurrences);
    }

    [Fact]
    public void Repeated_wording_with_nothing_to_tell_the_occurrences_apart_is_rejected_not_guessed()
    {
        var verdict = ClarificationCandidateValidator.ValidateOne(Context(), Candidate("ambiguous",
            "Does the seven year retention in the appointment register start at creation or at the appointment?",
            ["are stored in the appointment register for seven years"]));

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Rejections, r => r.Code == "ambiguous-location");
        Assert.Empty(verdict.Evidence);
    }

    // ---- generation: nothing rejected reaches a reader --------------------------------------------

    [Fact]
    public async Task Rejected_candidates_are_never_saved_shown_or_brought_back_by_reuse_or_duplicates()
    {
        var (db, source) = Seed();
        var grounded = V3Question("missing", "How long before the appointment is the reminder notification sent?",
            ["The system shall send a reminder notification to the patient before each appointment."]);
        var ai = new ScriptedAiChatClient(V3Response(
            grounded,
            V3Question("missing", "Who approves a refund for a cancelled booking?", ["Refunds for cancelled bookings are approved by the practice manager."]),
            V3Question("ambiguous", "Does the seven year retention in the appointment register start at creation or at the appointment?",
                ["are stored in the appointment register for seven years"]),
            V3Question("contradiction", "Is the cancellation deadline 24 hours or 72 hours before the appointment?",
                ["Patients may cancel an appointment up to 24 hours before it starts.", "Appointments can be cancelled up to 72 hours before the start time."]),
            // The same grounded question twice in one response: merged, never saved twice.
            grounded.DeepClone().AsObject()));

        var first = await Analysis(db, ai).AnalyzeAsync(source.Id);

        Assert.Single(first.ClarificationQuestions);
        Assert.Equal(4, first.RejectedCandidates!.Count);
        Assert.Contains(first.RejectedCandidates, r => r.Reasons.Any(x => x.Code == "ambiguous-location"));
        Assert.Contains(first.RejectedCandidates, r => r.Reasons.Any(x => x.Code == "duplicate"));
        Assert.Single(InMemoryAiReapDb.Rows(db.Artifacts));
        var shown = Assert.Single(await ReadAsync(db, source));
        Assert.Equal("How long before the appointment is the reminder notification sent?", shown.Value.GetProperty("question").GetString());

        // The stored analysis is reused: no AI call (the scripted client has no response left), nothing new.
        var reused = await Analysis(db, ai).AnalyzeAsync(source.Id);
        Assert.True(reused.Reused);
        Assert.Empty(reused.ClarificationQuestions);
        Assert.Single(await ReadAsync(db, source));
    }

    // ---- existing questions ---------------------------------------------------------------------

    [Fact]
    public async Task Legacy_questions_are_shown_only_with_verified_or_recoverable_evidence_and_are_never_changed()
    {
        var (db, source) = Seed();
        var verified = SaveQuestion(db, source, "CQ-001", new
        {
            Question = "What response time counts as prompt for a booking confirmation?", ClarificationStatus = "Answered", Answer = "Two minutes.",
            Kind = "ambiguous", Impact = "x", SourceEvidence = new[] { Placed(source.Id, "The portal shall confirm a booking promptly after it is submitted.") }
        });
        var recoverable = SaveQuestion(db, source, "CQ-002", new
        {
            Question = "What does 'export a daily report of appointments' include?", Reason = "The report content is not defined.", ClarificationStatus = "Open"
        });
        var unverifiable = SaveQuestion(db, source, "CQ-003", new
        {
            Question = "How are reminders for an appointment personalised?", Reason = "Personalisation is not described.", ClarificationStatus = "Open"
        });
        // Saved when the first occurrence was taken by default: nothing in the question singles it out.
        var guessed = SaveQuestion(db, source, "CQ-004", new
        {
            Question = "When does the seven year retention period start?", ClarificationStatus = "Open", Kind = "ambiguous", Impact = "x",
            SourceEvidence = new[] { Placed(source.Id, "are stored in the appointment register for seven years", onLine: "Bookings are stored") }
        });
        var saved = InMemoryAiReapDb.Rows(db.Artifacts).ToDictionary(a => a.Code, a => a.DataJson);

        var shown = await ReadAsync(db, source);

        Assert.Equal(["CQ-001", "CQ-002"], shown.Keys.Order());
        Assert.Equal("Two minutes.", shown["CQ-001"].GetProperty("answer").GetString());
        var recovered = shown["CQ-002"].GetProperty("sourceEvidence").Deserialize<List<SourceReferencePayload>>(Lenient)!;
        Assert.Equal(LineOf("FR-030"), Assert.Single(recovered).Line);
        // Nothing was written: hidden questions keep their records, answers and statuses.
        Assert.Equal(4, InMemoryAiReapDb.Rows(db.Artifacts).Count());
        Assert.All(InMemoryAiReapDb.Rows(db.Artifacts), a => Assert.Equal(saved[a.Code], a.DataJson));
        Assert.Equal([verified.Id, recoverable.Id, unverifiable.Id, guessed.Id], InMemoryAiReapDb.Rows(db.Artifacts).Select(a => a.Id));
    }

    [Fact]
    public async Task Stored_repeated_wording_stays_shown_where_the_question_still_cites_its_section()
    {
        var (db, source) = Seed();
        var section = SourceReferenceIndex.Build(source.Id, Srs).Resolve("Section 4 keeps reminder records.");
        SaveQuestion(db, source, "CQ-001", new
        {
            Question = "Are reminder records kept after the appointment is cancelled?", Reason = "Section 4 keeps reminder records.",
            ClarificationStatus = "Open", Kind = "missing", Impact = "x", SourceReferences = section,
            SourceEvidence = new[] { Placed(source.Id, "are stored in the appointment register for seven years", onLine: "Reminder records are stored") }
        });

        var shown = Assert.Single(await ReadAsync(db, source));
        var evidence = Assert.Single(shown.Value.GetProperty("sourceEvidence").Deserialize<List<SourceReferencePayload>>(Lenient)!);
        Assert.Equal(LineOf("Reminder records are stored"), evidence.Line);
    }

    // ---- reads are remembered, never served stale ---------------------------------------------------

    [Fact]
    public async Task A_repeated_read_returns_the_same_questions_and_links_as_the_first()
    {
        var (db, source) = Seed();
        SaveQuestion(db, source, "CQ-001", new
        {
            Question = "What response time counts as prompt for a booking confirmation?", ClarificationStatus = "Open", Kind = "ambiguous", Impact = "x",
            SourceEvidence = new[] { Placed(source.Id, "The portal shall confirm a booking promptly after it is submitted.") }
        });
        SaveQuestion(db, source, "CQ-002", new
        {
            Question = "What does 'export a daily report of appointments' include?", Reason = "The report content is not defined.", ClarificationStatus = "Open"
        });
        SaveQuestion(db, source, "CQ-003", new
        {
            Question = "How are reminders for an appointment personalised?", Reason = "Personalisation is not described.", ClarificationStatus = "Open"
        });

        var first = (await ReadAsync(db, source)).ToDictionary(p => p.Key, p => p.Value.GetRawText());
        var second = (await ReadAsync(db, source)).ToDictionary(p => p.Key, p => p.Value.GetRawText());

        Assert.Equal(["CQ-001", "CQ-002"], first.Keys.Order());
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Parallel_cold_reads_and_the_startup_warm_up_give_the_same_questions_as_one_read()
    {
        var (db, source) = Seed();
        SaveQuestion(db, source, "CQ-001", new
        {
            Question = "What response time counts as prompt for a booking confirmation?", ClarificationStatus = "Open", Kind = "ambiguous", Impact = "x",
            SourceEvidence = new[] { Placed(source.Id, "The portal shall confirm a booking promptly after it is submitted.") }
        });
        SaveQuestion(db, source, "CQ-002", new
        {
            Question = "What does 'export a daily report of appointments' include?", Reason = "The report content is not defined.", ClarificationStatus = "Open"
        });
        SaveQuestion(db, source, "CQ-003", new
        {
            Question = "How are reminders for an appointment personalised?", Reason = "Personalisation is not described.", ClarificationStatus = "Open"
        });

        // Questions of this test's own source only: the memory is shared by every test in the process.
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => ReadAsync(db, source))));
        var warmed = await ClarificationVisibility.WarmAsync(db, CancellationToken.None);
        var after = await ReadAsync(db, source);

        Assert.All(parallel, p => Assert.Equal(["CQ-001", "CQ-002"], p.Keys.Order()));
        Assert.All(parallel, p => Assert.Equal(parallel[0]["CQ-002"].GetRawText(), p["CQ-002"].GetRawText()));
        Assert.Equal(3, warmed);
        Assert.Equal(["CQ-001", "CQ-002"], after.Keys.Order());
        Assert.All(InMemoryAiReapDb.Rows(db.Artifacts), q =>
            Assert.True(ClarificationVisibility.Results.TryGet(q, ClarificationVisibility.Results.SourceGeneration(source.Id, source.RawText), out _)));
    }

    [Fact]
    public async Task An_edited_question_is_judged_again_on_the_next_read()
    {
        var (db, source) = Seed();
        var question = SaveQuestion(db, source, "CQ-001", new
        {
            Question = "How are reminders for an appointment personalised?", Reason = "Personalisation is not described.", ClarificationStatus = "Open"
        });
        Assert.Empty(await ReadAsync(db, source));

        // Same record, same version number: only its data changes, to a question its source verifies.
        question.DataJson = JsonSerializer.Serialize(new
        {
            Question = "What response time counts as prompt for a booking confirmation?", ClarificationStatus = "Open", Kind = "ambiguous", Impact = "x",
            SourceEvidence = new[] { Placed(source.Id, "The portal shall confirm a booking promptly after it is submitted.") }
        });

        Assert.Equal(["CQ-001"], (await ReadAsync(db, source)).Keys);
    }

    [Fact]
    public async Task A_question_whose_source_text_changed_is_linked_to_the_new_text()
    {
        var (db, source) = Seed();
        SaveQuestion(db, source, "CQ-001", new
        {
            Question = "What response time counts as prompt for a booking confirmation?", ClarificationStatus = "Open", Kind = "ambiguous", Impact = "x",
            SourceEvidence = new[] { Placed(source.Id, "The portal shall confirm a booking promptly after it is submitted.") }
        });
        Assert.Single(await ReadAsync(db, source));

        // The passage the question rests on is no longer in its source: it must not be shown from memory.
        source.RawText = Srs.Replace("FR-002 The portal shall confirm a booking promptly after it is submitted.", "FR-002 Bookings are confirmed by email.");

        Assert.Empty(await ReadAsync(db, source));
    }

    [Fact]
    public async Task A_hidden_question_cannot_surface_through_artifact_lists_lookups_dashboards_or_generation_context()
    {
        var (db, source) = Seed();
        var shown = SaveQuestion(db, source, "CQ-001", new
        {
            Question = "What response time counts as prompt for a booking confirmation?", ClarificationStatus = "Open", Kind = "ambiguous", Impact = "x",
            SourceEvidence = new[] { Placed(source.Id, "The portal shall confirm a booking promptly after it is submitted.") }
        });
        var hidden = SaveQuestion(db, source, "CQ-002", new
        {
            Question = "How are reminders for an appointment personalised?", Reason = "Personalisation is not described.", ClarificationStatus = "Open"
        });
        var answeredHidden = SaveQuestion(db, source, "CQ-003", new
        {
            Question = "Which colour scheme does the portal use?", ClarificationStatus = "Answered", Answer = "The clinic's blue palette."
        });
        var artifacts = new ArtifactService(db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoImpact());

        var listed = await artifacts.GetForProjectAsync(source.ProjectId, null, null);
        Assert.Equal(["CQ-001"], listed.Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion).Select(a => a.Code));
        Assert.NotNull(await artifacts.GetByIdAsync(shown.Id));
        Assert.Null(await artifacts.GetByIdAsync(hidden.Id));

        var dashboard = await new DashboardService(db, new NoProgress(), new OfflineCurrentUser()).GetAsync(source.ProjectId);
        Assert.Equal(1, dashboard.OpenClarificationCount);
        Assert.DoesNotContain(dashboard.RecentChanges, c => c.Code is "CQ-002" or "CQ-003");

        // Requirement generation is never told to assume a question no one can see; a given answer is kept.
        var context = string.Concat(await ClarificationContextBuilder.BuildItemsAsync(db, source.Id, CancellationToken.None));
        Assert.Contains("prompt for a booking confirmation", context);
        Assert.DoesNotContain("personalised", context);
        Assert.Contains("The clinic's blue palette.", context);
        Assert.Equal(3, InMemoryAiReapDb.Rows(db.Artifacts).Count());
        Assert.NotNull(answeredHidden);
    }

    [Fact]
    public async Task A_hidden_open_question_does_not_block_a_grounded_rewording_from_being_raised()
    {
        var (db, source) = Seed();
        SaveQuestion(db, source, "CQ-001", new
        {
            Question = "When is the reminder notification sent before the appointment?", Reason = "Timing is not stated.", ClarificationStatus = "Open"
        });
        Assert.Empty(await ReadAsync(db, source)); // nothing verifies it: hidden

        var ai = new ScriptedAiChatClient(V3Response(V3Question("missing", "How long before the appointment is the reminder notification sent?",
            ["The system shall send a reminder notification to the patient before each appointment."])));
        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);

        Assert.Single(result.ClarificationQuestions);
        Assert.Equal("How long before the appointment is the reminder notification sent?",
            Assert.Single(await ReadAsync(db, source)).Value.GetProperty("question").GetString());
        Assert.Equal(2, InMemoryAiReapDb.Rows(db.Artifacts).Count()); // the hidden record is kept as it was
    }

    [Fact]
    public void Reading_question_links_needs_no_AI_client()
    {
        // The read path and the visibility filter take no AI client at all - only the database.
        Assert.DoesNotContain(typeof(ClarificationService).GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(IAiChatClient));
        Assert.All(typeof(ClarificationVisibility).GetMethods().SelectMany(m => m.GetParameters()), p => Assert.NotEqual(typeof(IAiChatClient), p.ParameterType));
    }

    private sealed class NoImpact : IImpactAnalysisService
    {
        public Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> StageNoticesForChangeAsync(Artifact changed, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForProjectAsync(Guid projectId, bool includeAcknowledged, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImpactNoticeResponse?> AcknowledgeAsync(Guid noticeId, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoProgress : ISdlcProgressService
    {
        public Task<IReadOnlyList<SdlcPhaseProgress>> GetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SdlcPhaseProgress>>([]);
        public Task<IReadOnlyList<ProjectSdlcProgress>> GetManyAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProjectSdlcProgress>>([]);
    }
}
