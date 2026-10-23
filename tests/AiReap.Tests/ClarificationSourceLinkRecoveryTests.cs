using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// Every clarification question is linked to the passage of its SRS that raised it - when it is made
// (ClarificationCandidateValidator via RequirementAnalysisService) and when it is read
// (ClarificationService -> ClarificationSourceReferences.WithSourceLinks), including questions saved
// before evidence was kept. Offline: InMemoryAiReapDb, scripted AI responses, no SQL Server, no provider.
public class ClarificationSourceLinkRecoveryTests
{
    // Headings, numbered requirements, wording repeated in two sections ("are stored in the appointment
    // register"), an ambiguous term ("promptly"), a detail that is missing (the reminder channel), two
    // statements that conflict (24 vs 48 hours) and a line whose words a PDF extraction ran together.
    private const string Srs = """
        # Clinic Appointment System - Software Requirements Specification

        ## 1. Introduction
        This document specifies the clinic appointment system used by reception staff and patients.

        ## 2. Booking
        FR-001 Patients shall book appointments through the online portal.
        FR-002 The portal shall confirm a booking promptly after it is submitted.
        Bookings are stored in the appointment register for seven years.

        ## 3. Cancellation
        FR-010 Patients may cancel an appointment up to 24 hours before it starts.
        Cancelled appointments are released to the waiting list.

        ## 4. Reminders
        FR-020 The system shall send a reminder before each appointment.
        Reminder records are stored in the appointment register for seven years.
        Remindersaresentdaily to every patient with an appointment that week.

        ## 5. Policies
        ### 5.1 Cancellation policy
        Appointments can be cancelled up to 48 hours before the start time.

        ## 6. Reporting
        FR-030 Reception staff shall export a daily report of appointments.
        """;

    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Lines = SourceReferenceIndex.Lines(Srs);

    private static int LineOf(string text) => Array.FindIndex(Lines, l => l.Contains(text, StringComparison.Ordinal));

    // ---- helpers -----------------------------------------------------------------------------

    private static (InMemoryAiReapDb Db, RequirementSource Source) Seed()
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "CQ source links", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = Srs,
            CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        return (db, source);
    }

    // A question as an older release saved it: no kind, no evidence; references only when given.
    private static Artifact SaveQuestion(
        InMemoryAiReapDb db, RequirementSource source, string code, object payload,
        string? rawResponse = null, ArtifactStatus status = ArtifactStatus.AiGenerated, int version = 1, object? firstVersion = null)
    {
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = source.ProjectId, ArtifactType = ArtifactType.ClarificationQuestion, Code = code,
            Title = code, Status = status, Origin = ArtifactOrigin.Ai, RequirementSourceId = source.Id,
            DataJson = JsonSerializer.Serialize(payload), CurrentVersion = version, CreatedByUserId = "phase0-offline-user",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.Artifacts.Add(artifact);
        if (firstVersion is not null)
        {
            db.ArtifactVersions.Add(new ArtifactVersion
            {
                Id = Guid.NewGuid(), ArtifactId = artifact.Id, VersionNumber = 1, DataSnapshotJson = JsonSerializer.Serialize(firstVersion),
                ChangedByUserId = "phase0-offline-user", ChangedAt = DateTime.UtcNow, Origin = ArtifactOrigin.Ai
            });
        }

        if (rawResponse is not null)
        {
            db.AIExecutions.Add(new AIExecution
            {
                Id = Guid.NewGuid(), ProjectId = source.ProjectId, OperationType = "RequirementAnalysis", UserId = "phase0-offline-user",
                Timestamp = DateTime.UtcNow, Model = "older-model", PromptTemplateVersion = "RequirementAnalysis-v2",
                InputReference = source.Id.ToString(), OutputJson = rawResponse, ProducedArtifactId = artifact.Id
            });
        }

        return artifact;
    }

    private static string V2Response(string question, string reason, params (string Label, string? Section, string Quote)[] refs) => new JsonObject
    {
        ["actors"] = new JsonArray("Patient"),
        ["capabilities"] = new JsonArray("Book appointment"),
        ["missingInformation"] = new JsonArray(new JsonObject
        {
            ["topic"] = "Topic", ["question"] = question, ["reason"] = reason,
            ["sourceRefs"] = new JsonArray(refs.Select(r => (JsonNode)new JsonObject
            {
                ["label"] = r.Label, ["section"] = r.Section, ["heading"] = null, ["quote"] = r.Quote
            }).ToArray())
        })
    }.ToJsonString();

    private static async Task<Dictionary<string, JsonElement>> ReadAsync(InMemoryAiReapDb db, RequirementSource source)
    {
        var service = new ClarificationService(db, new AllowAllProjectAccess(), new OfflineCurrentUser());
        var questions = await service.GetForRequirementSourceAsync(source.Id);
        return questions.ToDictionary(q => q.Code, q => q.Data);
    }

    private static List<SourceReferencePayload> Evidence(JsonElement data) =>
        data.TryGetProperty("sourceEvidence", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.Deserialize<List<SourceReferencePayload>>(Lenient)!
            : [];

    private static List<SourceReferencePayload> Links(JsonElement data) =>
        data.GetProperty("sourceReferences").Deserialize<List<SourceReferencePayload>>(Lenient)!;

    private static string Status(JsonElement data) => data.GetProperty("sourceLinkStatus").GetString()!;

    private static ClarificationEvidenceLocator Locator(Guid sourceId) =>
        new(SourceReferenceIndex.Build(sourceId, Srs), SourceEvidenceIndex.Build(Srs), Srs);

    private static RequirementAnalysisService Analysis(InMemoryAiReapDb db, IAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(), new BatchModeOff(),
            NullLogger<RequirementAnalysisService>.Instance);

    private static string V3Response(params JsonObject[] questions) => new JsonObject
    {
        ["actors"] = new JsonArray("Patient", "Reception staff"),
        ["capabilities"] = new JsonArray("Book appointment"),
        ["dataElements"] = new JsonArray("Appointment"),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(questions.Cast<JsonNode>().ToArray())
    }.ToJsonString();

    private static JsonObject V3Question(string kind, string question, string reason, string[] evidence, JsonArray? sourceRefs = null) => new()
    {
        ["kind"] = kind, ["topic"] = question, ["question"] = question, ["reason"] = reason,
        ["impact"] = "The answer decides how the requirement is implemented and tested.",
        ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e).ToArray()),
        ["sourceRefs"] = sourceRefs ?? new JsonArray()
    };

    // ---- 1. Case A: a "Why it matters" that already cites its section ---------------------------

    [Fact]
    public async Task A_valid_section_reference_is_kept_and_the_motivating_paragraph_is_recovered_from_generation()
    {
        var (db, source) = Seed();
        const string reason = "Section 2 does not say how soon the booking confirmation must arrive.";
        var stored = SourceReferenceIndex.Build(source.Id, Srs).Resolve(reason);
        SaveQuestion(db, source, "CQ-001",
            new { Question = "How soon must the portal confirm a booking?", Reason = reason, ClarificationStatus = "Open", SourceReferences = stored },
            V2Response("How soon must the portal confirm a booking?", reason,
                ("Section 2", "2", "The portal shall confirm a booking promptly after it is submitted.")));

        var data = (await ReadAsync(db, source))["CQ-001"];

        var link = Assert.Single(Links(data));
        Assert.Equal("Section 2", link.Label);
        Assert.Equal(LineOf("## 2. Booking"), link.Line); // the stored, valid link is unchanged
        var evidence = Assert.Single(Evidence(data));
        Assert.Equal(LineOf("FR-002"), evidence.Line);
        Assert.Equal("The portal shall confirm a booking promptly after it is submitted.", evidence.Excerpt);
        Assert.True(evidence.ExcerptIsQuote);
        Assert.Equal(source.Id, evidence.SourceId);
        Assert.Equal(ClarificationSourceReferences.RecoveryGeneration, evidence.Recovery);
        Assert.Equal("passage", Status(data));
    }

    // ---- 2. Case B: "Why it matters" without any section reference ------------------------------

    [Fact]
    public async Task A_reason_without_a_section_reference_gets_its_passage_from_the_quote_the_model_gave()
    {
        // The model quoted the passage but its reason names no place, so the quote was dropped when saved.
        var (db, source) = Seed();
        const string reason = "Without a channel the reminder cannot be built or tested.";
        SaveQuestion(db, source, "CQ-002",
            new { Question = "Through which channel is the appointment reminder sent?", Reason = reason, ClarificationStatus = "Open", SourceReferences = Array.Empty<object>() },
            V2Response("Through which channel is the appointment reminder sent?", reason,
                ("the reminder", null, "The system shall send a reminder before each appointment.")));

        var data = (await ReadAsync(db, source))["CQ-002"];

        Assert.Empty(Links(data)); // the reason itself still names no place - nothing is invented in it
        var evidence = Assert.Single(Evidence(data));
        Assert.Equal(LineOf("FR-020"), evidence.Line);
        Assert.Equal("## 4. Reminders", evidence.Heading);
        Assert.Equal("passage", Status(data));
        // What it concerns, by the existing rule: IDs in the passage's excerpt, else its section.
        Assert.Equal(["4. Reminders"], data.GetProperty("requirementRefs").Deserialize<List<string>>());
    }

    [Fact]
    public async Task A_term_the_question_quotes_is_linked_as_the_sentence_holding_it_when_the_source_writes_it_once()
    {
        var (db, source) = Seed();
        SaveQuestion(db, source, "CQ-003",
            new { Question = "What response time does 'promptly' mean for a booking confirmation?", Reason = "The term is not measurable.", ClarificationStatus = "Open" });

        var data = (await ReadAsync(db, source))["CQ-003"];

        var evidence = Assert.Single(Evidence(data));
        Assert.Equal(LineOf("FR-002"), evidence.Line);
        Assert.Equal("FR-002 The portal shall confirm a booking promptly after it is submitted.", evidence.Excerpt);
        Assert.Equal(ClarificationSourceReferences.RecoveryQuoted, evidence.Recovery);
    }

    [Fact]
    public async Task A_requirement_id_the_question_names_is_linked_to_the_passage_defining_it()
    {
        var (db, source) = Seed();
        SaveQuestion(db, source, "CQ-004",
            new { Question = "Which format must the FR-030 report use?", Reason = "No format is given.", ClarificationStatus = "Open" });

        var data = (await ReadAsync(db, source))["CQ-004"];

        var evidence = Assert.Single(Evidence(data));
        Assert.Equal(LineOf("FR-030"), evidence.Line);
        Assert.Equal("FR-030 Reception staff shall export a daily report of appointments.", evidence.Excerpt);
        Assert.Equal(ClarificationSourceReferences.RecoveryRequirementId, evidence.Recovery);
    }

    // ---- 3. stale or invalid references --------------------------------------------------------

    [Fact]
    public async Task Stale_evidence_is_moved_back_onto_its_own_words_and_a_reference_that_no_longer_holds_is_replaced()
    {
        var (db, source) = Seed();
        const string reason = "Section 3 and the policy disagree.";
        SaveQuestion(db, source, "CQ-005", new
        {
            Question = "Is the cancellation deadline 24 or 48 hours?", Reason = reason, ClarificationStatus = "Open", Kind = "contradiction",
            Impact = "Decides when cancellation is refused.",
            // Wrong line, and an older fallback that saved no line or source at all.
            SourceEvidence = new[]
            {
                new SourceReferencePayload { Label = "x", SourceId = source.Id, Line = 1, EndLine = 2, Excerpt = "Patients may cancel an appointment up to 24 hours before it starts.", ExcerptIsQuote = true },
                new SourceReferencePayload { Label = "y", SourceId = Guid.Empty, Line = -1, EndLine = -1, Excerpt = "Appointments can be cancelled up to 48 hours before the start time.", ExcerptIsQuote = true }
            },
            // Points at a heading that is not on that line.
            SourceReferences = new[]
            {
                new SourceReferencePayload { Label = "Section 3", Start = 0, SourceId = source.Id, SectionNumber = "3", Heading = "## 3. Archive", Line = 2, EndLine = 4 }
            }
        });

        var data = (await ReadAsync(db, source))["CQ-005"];

        var evidence = Evidence(data);
        Assert.Equal([LineOf("FR-010"), LineOf("48 hours")], evidence.Select(e => e.Line));
        Assert.All(evidence, e => Assert.Equal(source.Id, e.SourceId));
        Assert.All(evidence, e => Assert.Contains(e.Excerpt!, Srs));
        var link = Assert.Single(Links(data));
        Assert.Equal("## 3. Cancellation", link.Heading);
        Assert.Equal(LineOf("## 3. Cancellation"), link.Line);
    }

    [Fact]
    public async Task A_quote_the_source_does_not_hold_is_never_placed_somewhere_else()
    {
        var (db, source) = Seed();
        SaveQuestion(db, source, "CQ-006", new
        {
            Question = "Who approves refunds?", Reason = "Refunds are not covered.", ClarificationStatus = "Open", Kind = "missing", Impact = "x",
            SourceEvidence = new[] { new SourceReferencePayload { Label = "q", SourceId = source.Id, Line = 3, EndLine = 4, Excerpt = "Refunds are approved by the practice manager.", ExcerptIsQuote = true } }
        });

        var data = (await ReadAsync(db, source))["CQ-006"];

        var evidence = Assert.Single(Evidence(data));
        Assert.Equal(-1, evidence.Line); // kept as given, marked as not located - never moved to similar words
        Assert.Equal("unresolved", Status(data));
    }

    // ---- 4-6. evidence made at generation time ---------------------------------------------------

    [Fact]
    public async Task An_ambiguous_statement_is_linked_exactly_and_highlighted_as_written()
    {
        var (db, source) = Seed();
        var ai = new ScriptedAiChatClient(V3Response(V3Question("ambiguous", "What response time counts as prompt for a booking confirmation?",
            "\"Promptly\" is not measurable.", ["The portal shall confirm a booking promptly after it is submitted."])));
        await Analysis(db, ai).AnalyzeAsync(source.Id);

        var data = Assert.Single(await ReadAsync(db, source)).Value;

        var evidence = Assert.Single(Evidence(data));
        Assert.Equal(LineOf("FR-002"), evidence.Line);
        Assert.Equal(evidence.Line + 1, evidence.EndLine); // the one paragraph, not the section
        Assert.Equal("The portal shall confirm a booking promptly after it is submitted.", evidence.Excerpt);
        Assert.Null(evidence.Recovery);
        Assert.Equal("passage", Status(data));
    }

    [Fact]
    public async Task A_conflict_is_linked_to_both_statements_in_their_own_sections()
    {
        var (db, source) = Seed();
        var ai = new ScriptedAiChatClient(V3Response(V3Question("contradiction", "Is the cancellation deadline 24 hours or 48 hours before the appointment?",
            "Section 3 and Section 5.1 give different deadlines.",
            ["Patients may cancel an appointment up to 24 hours before it starts.", "Appointments can be cancelled up to 48 hours before the start time."])));
        await Analysis(db, ai).AnalyzeAsync(source.Id);

        var data = Assert.Single(await ReadAsync(db, source)).Value;

        var evidence = Evidence(data);
        Assert.Equal([LineOf("FR-010"), LineOf("48 hours")], evidence.Select(e => e.Line));
        Assert.Equal(["## 3. Cancellation", "### 5.1 Cancellation policy"], evidence.Select(e => e.Heading));
        Assert.Equal(["Section 3", "Section 5.1"], Links(data).Select(l => l.Label));
    }

    [Fact]
    public async Task Missing_information_is_linked_to_its_context_without_pretending_the_absent_detail_is_written()
    {
        var (db, source) = Seed();
        var ai = new ScriptedAiChatClient(V3Response(V3Question("missing", "Through which channel (SMS or e-mail) is the reminder sent?",
            "The channel is not stated.", ["The system shall send a reminder before each appointment."])));
        await Analysis(db, ai).AnalyzeAsync(source.Id);

        var data = Assert.Single(await ReadAsync(db, source)).Value;

        Assert.Equal("missing", data.GetProperty("kind").GetString());
        var evidence = Assert.Single(Evidence(data));
        Assert.Equal(LineOf("FR-020"), evidence.Line);
        Assert.Contains(evidence.Excerpt!, Srs); // only text the source holds
        Assert.DoesNotContain("SMS", Srs);
    }

    [Fact]
    public async Task Evidence_whose_words_a_pdf_extraction_ran_together_is_still_placed()
    {
        // Verified word for word (SpacingTolerant), but not findable as typed: earlier this saved no line.
        var (db, source) = Seed();
        var ai = new ScriptedAiChatClient(V3Response(V3Question("ambiguous", "Which day of the week are the weekly reminders sent?",
            "\"That week\" does not say when reminders go out.", ["Reminders are sent daily to every patient with an appointment that week."])));
        await Analysis(db, ai).AnalyzeAsync(source.Id);

        var evidence = Assert.Single(Evidence(Assert.Single(await ReadAsync(db, source)).Value));
        Assert.Equal(LineOf("Remindersaresentdaily"), evidence.Line);
        Assert.Equal("Remindersaresentdaily to every patient with an appointment that week.", evidence.Excerpt); // the source's own text
    }

    // ---- 7. the same wording in several sections -----------------------------------------------

    [Fact]
    public void Repeated_wording_is_placed_in_the_section_the_generation_evidence_cites()
    {
        var sourceId = Guid.NewGuid();
        var context = new ClarificationCandidateValidator.Context(sourceId, Srs, []);
        var candidate = new ClarificationCandidate("Reminder retention", "How long are reminder records kept after an appointment is cancelled?",
            "Section 4 keeps reminder records but does not cover cancelled appointments.", "missing", "Decides the purge job.",
            ["stored in the appointment register for seven years"],
            [new ClaimedSourceReference { Label = "Section 4", Section = "4" }]);

        var verdict = ClarificationCandidateValidator.ValidateOne(context, candidate);

        var evidence = Assert.Single(verdict.Evidence);
        Assert.Equal(LineOf("Reminder records are stored"), evidence.Line); // not the Booking one, which comes first
        Assert.Equal(2, evidence.Occurrences);
    }

    [Fact]
    public async Task Repeated_wording_recovered_from_a_legacy_response_follows_the_section_the_model_claimed()
    {
        var (db, source) = Seed();
        const string reason = "Retention for reminders is unclear.";
        SaveQuestion(db, source, "CQ-007",
            new { Question = "Are reminder records kept after the appointment is cancelled?", Reason = reason, ClarificationStatus = "Open" },
            V2Response("Are reminder records kept after the appointment is cancelled?", reason,
                ("reminders", "4", "are stored in the appointment register for seven years")));

        var evidence = Assert.Single(Evidence((await ReadAsync(db, source))["CQ-007"]));
        Assert.Equal(LineOf("Reminder records are stored"), evidence.Line);
        Assert.Equal(2, evidence.Occurrences);
    }

    [Fact]
    public void Repeated_wording_with_nothing_to_tell_the_places_apart_says_how_many_there_are()
    {
        var locator = Locator(Guid.NewGuid());

        var evidence = Assert.Single(locator.Locate(["stored in the appointment register for seven years"]));

        Assert.Equal(LineOf("Bookings are stored"), evidence.Line);
        Assert.Equal(2, evidence.Occurrences); // the viewer says the wording appears twice
    }

    // ---- 9. unverifiable ------------------------------------------------------------------------

    [Fact]
    public async Task A_question_nothing_verifies_is_reported_unresolved_and_topic_words_are_never_matched()
    {
        var (db, source) = Seed();
        // "reminder" and "appointment" are all over the source, but nothing the question cites is.
        SaveQuestion(db, source, "CQ-008",
            new { Question = "How are reminders for an appointment personalised?", Reason = "Personalisation is not described.", ClarificationStatus = "Open" },
            V2Response("How are reminders for an appointment personalised?", "Personalisation is not described.",
                ("Section 9", "9", "Reminders are personalised with the patient's name.")));

        var data = (await ReadAsync(db, source))["CQ-008"];

        Assert.Empty(Evidence(data));
        Assert.Empty(Links(data));
        Assert.Equal("unresolved", Status(data));
    }

    // ---- 10-11. repair is idempotent and never touches the question's own fields -------------------

    [Fact]
    public async Task Reading_repairs_without_saving_and_reading_again_gives_the_same_links()
    {
        var (db, source) = Seed();
        const string reason = "Without a channel the reminder cannot be built or tested.";
        var response = V2Response("Through which channel is the appointment reminder sent?", reason,
            ("the reminder", null, "The system shall send a reminder before each appointment."),
            ("the reminder", null, "The system shall send a reminder before each appointment."));
        var artifact = SaveQuestion(db, source, "CQ-009",
            new { Question = "Through which channel is the appointment reminder sent?", Reason = reason, ClarificationStatus = "Open" }, response);
        var savedJson = artifact.DataJson;

        var first = (await ReadAsync(db, source))["CQ-009"];
        var second = (await ReadAsync(db, source))["CQ-009"];

        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Single(Evidence(first)); // the quote given twice is one passage
        Assert.Equal(savedJson, artifact.DataJson); // nothing was written
        Assert.Single(InMemoryAiReapDb.Rows(db.Artifacts));

        // Applying the repair to its own output changes nothing.
        var locator = Locator(source.Id);
        var once = ClarificationSourceReferences.WithSourceLinks(savedJson, source.Id, locator,
            new ClarificationSourceReferences.GenerationTrace(AnalysisResponseItems.Parse(response), null));
        Assert.Equal(once, ClarificationSourceReferences.WithSourceLinks(once, source.Id, locator));
    }

    [Fact]
    public async Task An_edited_answered_and_approved_question_only_gains_its_source_links()
    {
        var (db, source) = Seed();
        const string firstQuestion = "How soon must the portal confirm a booking?";
        var edited = new
        {
            Question = "Within how many minutes must the portal confirm a booking?", // reworded by a reviewer
            Reason = "The confirmation time is not stated.", ClarificationStatus = "Resolved", Answer = "Within 5 minutes."
        };
        SaveQuestion(db, source, "CQ-010", edited,
            V2Response(firstQuestion, "Not measurable.", ("x", null, "The portal shall confirm a booking promptly after it is submitted.")),
            status: ArtifactStatus.Approved, version: 2, firstVersion: new { Question = firstQuestion, Reason = "Not measurable.", ClarificationStatus = "Open" });

        var data = (await ReadAsync(db, source))["CQ-010"];

        Assert.Equal(edited.Question, data.GetProperty("question").GetString());
        Assert.Equal(edited.Reason, data.GetProperty("reason").GetString());
        Assert.Equal("Resolved", data.GetProperty("clarificationStatus").GetString());
        Assert.Equal("Within 5 minutes.", data.GetProperty("answer").GetString());
        Assert.Equal(LineOf("FR-002"), Assert.Single(Evidence(data)).Line); // matched by its first wording
    }

    [Fact]
    public void Analysis_responses_are_read_back_in_single_fenced_and_merged_forms()
    {
        var single = V2Response("Q one?", "R", ("l", null, "The system shall send a reminder before each appointment."));
        var merged = JsonSerializer.Serialize(new { parts = new[] { new { part = 1, of = 2, model = "m", response = "```json\n" + single + "\n```" } } });

        Assert.Equal("Q one?", Assert.Single(AnalysisResponseItems.Parse(single)).Question);
        Assert.Equal(["The system shall send a reminder before each appointment."], Assert.Single(AnalysisResponseItems.Parse(merged)).Quotes);
        Assert.Empty(AnalysisResponseItems.Parse("not json"));
    }
}
