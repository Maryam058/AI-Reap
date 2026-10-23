using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Knowledge;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

// §7/§8 — one analysis per requirement source, reused until someone explicitly regenerates it.
//   - AnalyzeAsync returns the stored analysis (RequirementAnalysisRecord) without an AI call when one
//     exists - also when it is stale (source text or analysis version changed, or coverage incomplete):
//     the result lists why, and only an explicit regenerate sends the source again.
//   - Every question the model proposes goes through ClarificationCandidateValidator before it is saved;
//     the rejected ones are kept in the record with their reasons, never saved as questions.
//   - Regenerating reconciles: a candidate that duplicates a saved question (any status) is not added,
//     and no saved question, answer or review is changed or removed.
public class RequirementAnalysisService : IRequirementAnalysisService, IChunkRunOperation
{
    internal const string PromptTemplateVersion = "RequirementAnalysis-v3";

    // Project-document retrieval per accepted question (KnowledgeRetriever): at most this many queries per
    // run, and only passages at or above the score are attached - as material a reviewer may want to see,
    // never as a reason to accept or reject a question.
    internal const int MaxRetrievalQueries = 10;
    internal const int RelatedDocumentsPerQuestion = 2;
    internal const float RelatedDocumentMinScore = 0.35f;

    // Shared with the dry-run AI plan (AiPlanService), so it plans the prompt a real run sends.
    internal const string SystemPrompt = """
        You are a requirements analyst (§7 of an SDLC automation spec). Read the whole requirement
        text - every section, table, glossary, appendix, constraint and summary - and decompose it into
        actors, capabilities, data elements the system must track, and informal notes/rules you observed.

        Then list in missingInformation ONLY the clarification questions that are genuinely needed. A
        question is justified only by a material, unresolved issue in the text, of one of these kinds:
          "missing"        information a requirement or an implementation decision cannot proceed
                           without (an amount, a limit, a responsible role, a time period) is absent;
          "ambiguous"      a statement can reasonably be read in materially different ways, or relies
                           on a term the text never defines;
          "contradiction"  two statements in the text conflict with each other;
          "undefined-rule" a business rule or acceptance condition that materially affects expected
                           behaviour is referred to but not defined.
        Do NOT ask:
          - anything the text answers anywhere - check every other section before asking;
          - generic best-practice or open-ended questions ("any other requirements?");
          - questions whose answer would not change a requirement, design, implementation or acceptance;
          - two questions about the same issue, or several questions in one.
        An empty missingInformation list is the correct answer when nothing material is unresolved.
        Never invent a confirmed detail (a number, a rule, a permission) that was not stated.

        For each question give:
          "kind"     - one of the four kinds above;
          "question" - one neutral, answerable question ending with "?";
          "reason"   - what exactly is missing, ambiguous or conflicting;
          "impact"   - why the answer matters to requirements, design, implementation or acceptance;
          "evidence" - one to three passages copied word for word from the text that show the issue,
                       each at least four words and without ellipses. A contradiction must quote BOTH
                       conflicting statements.

        When a reason refers to a place in the requirement text, list it in that item's
        sourceRefs: "label" is the exact words of the reason that refer to it (e.g. "Section 3.2"),
        "section" its section number and "heading" its heading exactly as written in the text, and
        "quote" a short passage copied word for word from that place. Only reference sections,
        headings and passages that are actually written in the text, never a page number; leave
        sourceRefs empty when the reason refers to no specific place.

        Respond with ONLY a single JSON object, no markdown fences, no commentary, matching
        exactly this shape:
        {
          "actors": ["string"],
          "capabilities": ["string"],
          "dataElements": ["string"],
          "notes": ["string"],
          "missingInformation": [{"kind": "missing|ambiguous|contradiction|undefined-rule", "topic": "string",
            "question": "string", "reason": "string", "impact": "string", "evidence": ["string"],
            "sourceRefs": [{"label": "string", "section": "string", "heading": "string", "quote": "string"}]}]
        }
        """;

    internal const string UnrecordedAnalysisReason =
        "This source was analysed before analysis results were recorded, so the rules it was checked against are unknown. " +
        "Its clarification questions are kept; regenerate to check the source against the current rules.";

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IKnowledgeRetriever _retriever;
    private readonly IAiBatchSettings _batchSettings;
    private readonly ILogger<RequirementAnalysisService> _logger;

    public RequirementAnalysisService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IKnowledgeRetriever retriever, IAiBatchSettings batchSettings, ILogger<RequirementAnalysisService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _retriever = retriever;
        _batchSettings = batchSettings;
        _logger = logger;
    }

    public AiChunkRunOperation Operation => AiChunkRunOperation.Analyze;

    public Task<AnalyzeRequirementResult> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default) =>
        AnalyzeAsync(requirementSourceId, regenerate: false, cancellationToken);

    public async Task<AnalyzeRequirementResult> AnalyzeAsync(Guid requirementSourceId, bool regenerate, CancellationToken cancellationToken = default)
    {
        var source = await LoadAsync(requirementSourceId, cancellationToken);
        if (!regenerate && await StoredAsync(source, cancellationToken) is { } stored)
        {
            _logger.LogInformation("RequirementAnalysis for source {SourceId}: stored analysis returned, no AI call (stale: {Stale}).",
                source.Id, stored.Stale);
            return stored;
        }

        await AiChunkRunGuard.EnsureNoActiveRunAsync(_db, _batchSettings, source.Id, Operation, cancellationToken);

        // A source too large for the request budget of the provider serving the call (Groq) is
        // analysed in parts and merged here, never condensed; Gemini gets the whole source as before.
        var parts = await GenerationSupport.CallAiAndParseChunkedAsync<AnalysisAiResponse, AnalysisPartAiResponse>(
            _chatClient, _logger, "RequirementAnalysis", source.Id.ToString(), SystemPrompt, source.RawText, cancellationToken);
        var (parsed, rawResponse, model) = parts.Count == 1
            ? (parts[0].Parsed, parts[0].RawResponse, parts[0].Model)
            : Merge(parts);

        var coverage = AnalysisCoverage.Assess(source.RawText, parts.Select(p => p.SourceText).ToList());
        var completeness = RequirementCompleteness.ForParts(parts, clarificationCount: 0);
        var (record, questions) = await PersistAsync(
            source, parsed, rawResponse, model, coverage, completeness, regenerate, onPrepared: null, cancellationToken);

        return ToResult(record, source, questions, reused: false);
    }

    public async Task<AnalyzeRequirementResult?> GetLatestAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await LoadAsync(requirementSourceId, cancellationToken);
        return await StoredAsync(source, cancellationToken);
    }

    private async Task<RequirementSource> LoadAsync(Guid requirementSourceId, CancellationToken cancellationToken)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        return source;
    }

    private async Task<AnalyzeRequirementResult?> StoredAsync(RequirementSource source, CancellationToken cancellationToken)
    {
        if (await RequirementAnalysisRecords.LatestAsync(_db, source, cancellationToken) is { } record)
        {
            return ToResult(record, source, [], reused: true);
        }

        return await RequirementAnalysisRecords.HasUnrecordedAnalysisAsync(_db, source, cancellationToken)
            ? new AnalyzeRequirementResult([], [], [], [], [], Reused: true, StaleReasons: [UnrecordedAnalysisReason])
            : null;
    }

    private static AnalyzeRequirementResult ToResult(
        RequirementAnalysisRecord record, RequirementSource source, IReadOnlyList<Artifact> created, bool reused) => new(
        record.Actors,
        record.Capabilities,
        record.DataElements,
        record.Notes,
        created.Select(ArtifactResponseMapper.ToResponse).ToList(),
        record.SkippedDuplicates,
        record.Completeness,
        reused,
        RequirementAnalysisRecords.StaleReasons(record, source),
        record.AnalysisVersion,
        record.AnalyzedAt,
        record.Coverage,
        record.Rejected,
        record.Retrieval,
        record.Reconciliation);

    // One save (ArtifactPersistence): the accepted questions, their versions and fingerprints, the raw
    // response audit rows and the analysis record. Validation runs inside the save's preparation, so a
    // unique-index race re-validates against the questions the other request saved.
    private async Task<(RequirementAnalysisRecord Record, List<Artifact> Questions)> PersistAsync(
        RequirementSource source, AnalysisPartAiResponse parsed, string rawResponse, string model,
        AnalysisCoverageReport coverage, RequirementCompleteness? completeness, bool regenerated,
        Action<ChunkRunCommitSummary>? onPrepared, CancellationToken cancellationToken)
    {
        var candidates = parsed.MissingInformation.Select(ToCandidate).ToList();

        // Retrieval is I/O (an embedding call per query), so it runs once, before the save, for the
        // candidates that pass against what is saved now; the save re-validates them.
        var preview = ClarificationCandidateValidator.Validate(
            new ClarificationCandidateValidator.Context(source.Id, source.RawText,
                (await ExistingQuestionsAsync(source.Id, cancellationToken)).Select(e => e.Existing)),
            candidates);
        var (related, retrieval) = await RetrieveRelatedAsync(source, preview.Where(v => v.Accepted).ToList(), cancellationToken);

        var record = new RequirementAnalysisRecord();
        var questionArtifacts = new List<Artifact>();

        await ArtifactPersistence.SaveAsync(_db, async ct =>
        {
            var now = DateTime.UtcNow;
            questionArtifacts = [];

            var existing = await ExistingQuestionsAsync(source.Id, ct);
            var context = new ClarificationCandidateValidator.Context(source.Id, source.RawText, existing.Select(e => e.Existing));
            var verdicts = ClarificationCandidateValidator.Validate(context, candidates);

            // The exact fingerprint stays as the database-enforced guard (unique index) against a
            // concurrent identical save.
            var fingerprints = await ArtifactFingerprints.ExistingAsync(_db, source.Id, [ArtifactType.ClarificationQuestion], ct);
            var accepted = new List<(CandidateVerdict Verdict, string Fingerprint)>();
            var rejected = new List<RejectedClarification>();
            var skipped = 0;
            foreach (var verdict in verdicts)
            {
                var reasons = verdict.Rejections.ToList();
                var fingerprint = ArtifactFingerprints.ForClarification(verdict.Candidate.Question);
                if (verdict.Accepted && !fingerprints.Add((ArtifactType.ClarificationQuestion, fingerprint)))
                {
                    reasons.Add(new CandidateRejection("duplicate", "The same question is already saved for this source."));
                }

                if (reasons.Count == 0)
                {
                    accepted.Add((verdict, fingerprint));
                    continue;
                }

                if (reasons.Any(r => r.Code is "duplicate" or "already-answered")) skipped++;
                rejected.Add(new RejectedClarification(verdict.Candidate.Topic, verdict.Candidate.Question, verdict.Kind, reasons));
            }

            var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
                _db, source.ProjectId, ArtifactType.ClarificationQuestion, accepted.Count, ct);
            for (var i = 0; i < accepted.Count; i++)
            {
                var (verdict, fingerprint) = accepted[i];
                var item = parsed.MissingInformation[candidates.IndexOf(verdict.Candidate)];
                var payload = new ClarificationQuestionPayload
                {
                    Question = verdict.Candidate.Question.Trim(),
                    Reason = verdict.Candidate.Reason,
                    ClarificationStatus = "Open",
                    // Only the references that resolve in the source; the model's claims are not stored.
                    SourceReferences = context.References.Resolve(item.Reason, item.SourceRefs),
                    Kind = verdict.Kind,
                    Impact = verdict.Candidate.Impact,
                    SourceEvidence = verdict.Evidence.ToList(),
                    RequirementRefs = verdict.RequirementRefs.ToList(),
                    RelatedDocuments = related.TryGetValue(AiResultMerge.Key(verdict.Candidate.Question), out var passages) ? passages : null
                };

                var artifact = new Artifact
                {
                    Id = Guid.NewGuid(),
                    ProjectId = source.ProjectId,
                    ArtifactType = ArtifactType.ClarificationQuestion,
                    Code = codes[i],
                    Title = verdict.Candidate.Topic,
                    Status = ArtifactStatus.AiGenerated,
                    Origin = ArtifactOrigin.Ai,
                    RequirementSourceId = source.Id,
                    DataJson = JsonSerializer.Serialize(payload),
                    CurrentVersion = 1,
                    CreatedByUserId = _currentUser.UserId,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _db.Artifacts.Add(artifact);
                _db.ArtifactVersions.Add(new ArtifactVersion
                {
                    Id = Guid.NewGuid(),
                    ArtifactId = artifact.Id,
                    VersionNumber = 1,
                    DataSnapshotJson = artifact.DataJson,
                    Title = artifact.Title,
                    Priority = artifact.Priority,
                    Status = artifact.Status,
                    ChangedByUserId = _currentUser.UserId,
                    ChangedAt = now,
                    Reason = "AI requirement analysis",
                    Origin = ArtifactOrigin.Ai
                });
                _db.ArtifactFingerprints.Add(ArtifactFingerprints.Record(artifact, fingerprint, now));

                questionArtifacts.Add(artifact);
            }

            var created = questionArtifacts.Select(a => a.Id).ToList();
            record = new RequirementAnalysisRecord
            {
                AnalysisVersion = RequirementAnalysisRecords.AnalysisVersion,
                SourceHash = RequirementAnalysisRecords.SourceHash(source.RawText),
                SourceLength = source.RawText.Length,
                AnalyzedAt = now,
                Regenerated = regenerated,
                Model = model,
                Actors = parsed.Actors,
                Capabilities = parsed.Capabilities,
                DataElements = parsed.DataElements,
                Notes = parsed.Notes,
                Completeness = completeness,
                Coverage = coverage,
                Retrieval = retrieval,
                Reconciliation = regenerated ? Reconcile(existing, rejected) : null,
                AcceptedQuestionIds = created,
                Rejected = rejected,
                SkippedDuplicates = skipped
            };

            GenerationSupport.AddExecutions(
                _db, source.ProjectId, "RequirementAnalysis", PromptTemplateVersion, _currentUser.UserId,
                model, source.Id.ToString(), rawResponse, now, created);
            RequirementAnalysisRecords.Add(_db, source, record, _currentUser.UserId);
            onPrepared?.Invoke(new ChunkRunCommitSummary(created, skipped));
        }, cancellationToken);

        _logger.LogInformation(
            "RequirementAnalysis for source {SourceId}: {Candidates} candidate question(s), {Accepted} accepted, {Rejected} rejected " +
            "({Skipped} duplicates of saved questions); coverage {Coverage}; project-document retrieval {Retrieval}.",
            source.Id, candidates.Count, questionArtifacts.Count, record.Rejected.Count, record.SkippedDuplicates,
            coverage.Status, retrieval.ProjectDocuments);

        return (record, questionArtifacts);
    }

    private sealed record ExistingQuestion(Artifact Artifact, ExistingClarification Existing);

    // Every question saved for the source - answered, resolved, not applicable or rejected included - so a
    // re-run neither repeats nor resurrects one.
    private async Task<List<ExistingQuestion>> ExistingQuestionsAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var artifacts = await _db.Artifacts.AsNoTracking()
            .Where(a => a.RequirementSourceId == sourceId && a.ArtifactType == ArtifactType.ClarificationQuestion)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        return artifacts.Select(a =>
        {
            ClarificationQuestionPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(a.DataJson, options) ?? new();
            }
            catch (JsonException)
            {
                payload = new();
            }

            var lines = new HashSet<int>();
            foreach (var reference in payload.SourceEvidence ?? [])
            {
                for (var l = reference.Line; l >= 0 && l < Math.Max(reference.EndLine, reference.Line + 1); l++) lines.Add(l);
            }

            var status = a.Status == ArtifactStatus.Rejected ? "Rejected" : payload.ClarificationStatus;
            return new ExistingQuestion(a, new ExistingClarification(
                a.Code, payload.Question, a.Title, status, lines, payload.Kind, payload.Reason, payload.Answer, payload.SourceReferences));
        }).ToList();
    }

    private static AnalysisReconciliation Reconcile(List<ExistingQuestion> existing, List<RejectedClarification> rejected)
    {
        var raisedAgain = rejected.SelectMany(r => r.Reasons)
            .Where(r => r.Code is "duplicate" or "already-answered" && r.RelatedCode is not null)
            .Select(r => r.RelatedCode!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var notRaisedAgain = existing
            .Where(e => e.Existing.Status == "Open" && e.Artifact.Origin == ArtifactOrigin.Ai && !raisedAgain.Contains(e.Existing.Code))
            .Select(e => e.Existing.Code)
            .ToList();
        return new AnalysisReconciliation(existing.Count, raisedAgain, notRaisedAgain);
    }

    private async Task<(Dictionary<string, List<RelatedDocumentPassagePayload>> Related, AnalysisRetrievalReport Report)> RetrieveRelatedAsync(
        RequirementSource source, IReadOnlyList<CandidateVerdict> accepted, CancellationToken cancellationToken)
    {
        var related = new Dictionary<string, List<RelatedDocumentPassagePayload>>(StringComparer.Ordinal);
        if (accepted.Count == 0)
        {
            return (related, new AnalysisRetrievalReport("lexical", "not-run", 0, 0));
        }

        var status = "used";
        int queries = 0, attached = 0;
        try
        {
            foreach (var verdict in accepted.Take(MaxRetrievalQueries))
            {
                var query = $"{verdict.Candidate.Question}\n{verdict.Candidate.Reason}";
                var result = await _retriever.RetrieveAsync(source.ProjectId, query, RelatedDocumentsPerQuestion, cancellationToken);
                if (!result.HasDocuments)
                {
                    status = "no-documents";
                    break;
                }

                if (result.Chunks.Count == 0 && result.StaleChunkCount > 0)
                {
                    status = "stale-only";
                    break;
                }

                queries++;
                var passages = result.Chunks
                    .Where(c => c.Score >= RelatedDocumentMinScore)
                    .Select(c => new RelatedDocumentPassagePayload
                    {
                        Reference = c.Reference,
                        Score = c.Score,
                        Excerpt = c.ChunkText.Length <= 400 ? c.ChunkText : c.ChunkText[..397] + "..."
                    })
                    .ToList();
                if (passages.Count > 0)
                {
                    related[AiResultMerge.Key(verdict.Candidate.Question)] = passages;
                    attached += passages.Count;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Retrieval only adds context for reviewers: the questions are still saved, and the record
            // says retrieval failed rather than implying it ran.
            _logger.LogWarning(ex, "RequirementAnalysis for source {SourceId}: project-document retrieval failed.", source.Id);
            return (new(StringComparer.Ordinal), new AnalysisRetrievalReport("lexical", "failed", queries, 0, ex.Message));
        }

        return (related, new AnalysisRetrievalReport("lexical", status, queries, attached));
    }

    private static ClarificationCandidate ToCandidate(MissingInformationItem item) =>
        new(item.Topic, item.Question, item.Reason, item.Kind, item.Impact, EvidenceQuotes(item.Evidence), item.SourceRefs);

    // Evidence as the prompt asks (strings), or as objects carrying a "quote" a model may return instead.
    internal static List<string> EvidenceQuotes(List<JsonElement>? evidence)
    {
        var quotes = new List<string>();
        foreach (var element in evidence ?? [])
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
            {
                quotes.Add(text);
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("quote", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        quotes.Add(property.Value.GetString()!);
                    }
                }
            }
        }

        return quotes;
    }

    // ---- batched runs (IChunkRunOperation) ---------------------------------------------------

    public Task<ChunkRunInputs> BuildRunInputsAsync(RequirementSource source, CancellationToken cancellationToken) =>
        Task.FromResult(new ChunkRunInputs(
            PromptTemplateVersion, SystemPrompt, source.RawText, string.Empty, GenerationSupport.SharedContext.None,
            [], [], ValidateSourceIds: false, ContextMaterial()));

    public Task<string> ContextMaterialAsync(RequirementSource source, CancellationToken cancellationToken) =>
        Task.FromResult(ContextMaterial());

    private static string ContextMaterial() => PromptTemplateVersion + "\u001e" + SystemPrompt;

    public async Task<ChunkRunPartResult> SendPartAsync(ChunkRunPartRequest request, CancellationToken cancellationToken)
    {
        var (raw, parsed) = await GenerationSupport.CallPlannedPartAsync<AnalysisAiResponse, AnalysisPartAiResponse>(
            _chatClient, _logger, "RequirementAnalysis", $"{request.RequirementSourceId}#run-{request.Frozen.RunId}",
            request.Frozen.SystemPrompt, request.Prompt, request.Number, request.Count, request.Delay,
            null, null, null, cancellationToken);
        return new ChunkRunPartResult(raw, JsonSerializer.Serialize(parsed), _chatClient.ModelName);
    }

    // A batched run is started explicitly, so committing it counts as a regeneration. The part texts are
    // not kept to commit time, so its coverage is reported "unverified" rather than claimed.
    public async Task<ChunkRunCommitSummary> CommitAsync(
        RequirementSource source, ChunkRunFrozen frozen, IReadOnlyList<ChunkRunPartOutput> parts,
        Action<ChunkRunCommitSummary> onPrepared, CancellationToken cancellationToken)
    {
        var aiParts = parts.Select(p => new GenerationSupport.AiPart<AnalysisPartAiResponse>(
                p.Number, p.Count, p.RawResponse,
                JsonSerializer.Deserialize<AnalysisPartAiResponse>(p.ParsedJson) ?? new AnalysisPartAiResponse(),
                p.Model, p.Section))
            .ToList();
        var (parsed, rawResponse, model) = aiParts.Count == 1
            ? (aiParts[0].Parsed, aiParts[0].RawResponse, aiParts[0].Model)
            : Merge(aiParts);
        var coverage = AnalysisCoverage.Assess(source.RawText, aiParts.Select(_ => (string?)null).ToList());
        var (record, questions) = await PersistAsync(source, parsed, rawResponse, model, coverage,
            RequirementCompleteness.ForParts(aiParts, clarificationCount: 0), regenerated: true, onPrepared, cancellationToken);
        return new ChunkRunCommitSummary(questions.Select(q => q.Id).ToList(), record.SkippedDuplicates);
    }

    // Parts are merged in source order with duplicates removed (AiResultMerge). The audit keeps
    // every part's raw response and the model(s) that served them.
    private static (AnalysisPartAiResponse Parsed, string RawResponse, string Model) Merge(
        IReadOnlyList<GenerationSupport.AiPart<AnalysisPartAiResponse>> parts)
    {
        var merged = new AnalysisPartAiResponse
        {
            Actors = AiResultMerge.DistinctTexts(parts.SelectMany(p => p.Parsed.Actors)),
            Capabilities = AiResultMerge.DistinctTexts(parts.SelectMany(p => p.Parsed.Capabilities)),
            DataElements = AiResultMerge.DistinctTexts(parts.SelectMany(p => p.Parsed.DataElements)),
            Notes = AiResultMerge.DistinctTexts(parts.SelectMany(p => p.Parsed.Notes)),
            MissingInformation = AiResultMerge.DistinctQuestions(
                parts.SelectMany(p => p.Parsed.MissingInformation), m => m.Topic, m => m.Question,
                m => m.Reason, (m, reason) => m.Reason = reason)
        };

        if (merged.Actors.Count == 0 && merged.Capabilities.Count == 0 && merged.MissingInformation.Count == 0)
        {
            const string empty = "The analysis is empty: no actors, capabilities or missing information were identified in any part of the requirement source.";
            throw new AiOutputValidationException(empty, [empty], repairable: false);
        }

        var rawResponse = JsonSerializer.Serialize(new
        {
            parts = parts.Select(p => new { part = p.Number, of = p.Count, model = p.Model, response = p.RawResponse })
        });
        var model = string.Join(" + ", parts.Select(p => p.Model).Distinct());
        return (merged, rawResponse, model);
    }

    // One part of a split source: it may legitimately be all background prose, so emptiness is
    // judged on the merged result instead (Merge).
    private class AnalysisPartAiResponse : IValidatableAiResponse
    {
        protected virtual bool RequireFindings => false;

        public List<string> Actors { get; set; } = new();
        public List<string> Capabilities { get; set; } = new();
        public List<string> DataElements { get; set; } = new();
        public List<string> Notes { get; set; } = new();
        public List<MissingInformationItem> MissingInformation { get; set; } = new();

        // Shape only. Whether a question is worth saving is decided by ClarificationCandidateValidator,
        // which rejects a bad question without failing (or repairing) the whole response.
        public void Validate(AiResponseValidator v)
        {
            v.NoBlankEntries(Actors, "actors");
            v.NoBlankEntries(Capabilities, "capabilities");
            v.NoBlankEntries(DataElements, "dataElements");
            v.Items(MissingInformation, "missingInformation", (item, path) =>
            {
                v.Required(item.Topic, $"{path}.topic");
                v.MaxLength(item.Topic, 300, $"{path}.topic");
                v.Required(item.Question, $"{path}.question");
            });
            if (RequireFindings && Actors.Count == 0 && Capabilities.Count == 0 && MissingInformation.Count == 0)
            {
                v.Fail("The analysis is empty: no actors, capabilities or missing information were identified.");
            }
        }
    }

    // A response for the whole source must find something.
    private class AnalysisAiResponse : AnalysisPartAiResponse
    {
        protected override bool RequireFindings => true;
    }

    private class MissingInformationItem
    {
        public string? Kind { get; set; }
        public string Topic { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string? Impact { get; set; }
        public List<JsonElement>? Evidence { get; set; }
        public List<ClaimedSourceReference>? SourceRefs { get; set; }
    }
}
