using System.Text.Json;
using System.Text.Json.Serialization;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Knowledge;
using AiReap.Application.Persistence;
using AiReap.Application.Traceability;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public class RequirementGenerationService : IRequirementGenerationService, IChunkRunOperation
{
    internal const string PromptTemplateVersion = "RequirementGeneration-v4";

    // The system prompt, the context headings and DocumentContext are shared with the dry-run AI
    // plan (AiPlanService) and batched runs (AiChunkRunService), so they plan and send the prompt a
    // normal run sends. v3 = v2 plus sourceIds / dependsOnSourceIds; nothing was removed. v4 (P0-2)
    // = v3 plus requirementOrigin, derivationNote, assumedClarifications, evidence and targetOrigin,
    // with clarifications listed by code; assumptionStatus is no longer asked for (the server always
    // decided it - PersistAsync).
    internal const string SystemPrompt = """
        You are a requirements engineer (§9/§10 of an SDLC automation spec). Given a raw
        requirement and any clarifications gathered so far, produce structured functional and
        non-functional requirements.

        Classify every requirement with requirementOrigin:
          "explicit" - the requirement text states it;
          "derived"  - it follows from stated text but is not written as such (explain in derivationNote);
          "assumed"  - it depends on an unanswered clarification or on nothing in the input
                       (explain in derivationNote and list those clarifications' codes in assumedClarifications).
        Explanatory or background text is not a requirement; do not turn it into one.

        Give evidence: 1-2 short verbatim quotes (at most 40 words each), copied exactly from the input.
          from "srs" for the requirement text (sourceId: the requirement ID it sits under, if any),
          from "clarification" for a clarification answer (ref: its code, e.g. "CQ-003"),
          from "excerpt" for a project document excerpt (ref: its label, e.g. "notes.pdf#3").
        Never paraphrase inside a quote. Use [] when there is nothing to quote.

        For non-functional requirements: set targetValue only to a measurable target. Set targetOrigin
        "stated" only if that exact target is written in the input and quoted in evidence; otherwise, if you
        propose a target, set targetOrigin "proposed". Never present a proposed target as stated.

        If project document excerpts are provided, use them only to inform requirements that the
        raw requirement or clarifications support, and list the labels of the excerpts a
        requirement relies on in sourceReferences (e.g. "notes.pdf#3"); use [] otherwise.

        If business objectives are listed, set businessObjectiveCodes on each functional
        requirement to the codes (e.g. "BO-001") of the objectives it directly serves. Use only
        codes from that list; use [] when none clearly applies - never invent a code.

        If the requirement text labels requirements with identifiers (e.g. "FR-012", "REQ-3.2.1"),
        set sourceIds on each requirement to the identifiers, exactly as written in the text you
        were given, of the statements it is derived from, and dependsOnSourceIds on a functional
        requirement to the identifiers of other requirements it explicitly depends on. Cite only
        identifiers written in the requirement text; use [] when there are none.

        Respond with ONLY a single JSON object, no markdown fences, no commentary, matching
        exactly this shape:
        {
          "functionalRequirements": [
            {"title": "string", "actor": "string", "priority": "Low|Medium|High|Critical",
             "preconditions": "string", "inputs": "string", "processing": "string",
             "expectedResult": "string", "dependencies": ["string"], "businessObjectiveCodes": ["BO-001"], "sourceReferences": ["notes.pdf#0"],
             "sourceIds": ["FR-012"], "dependsOnSourceIds": ["FR-003"],
             "requirementOrigin": "explicit|derived|assumed", "derivationNote": "string or null", "assumedClarifications": ["CQ-003"],
             "evidence": [{"from": "srs|clarification|excerpt", "quote": "string", "sourceId": "FR-012 or null", "ref": "CQ-003 or notes.pdf#0 or null"}]}
          ],
          "nonFunctionalRequirements": [
            {"title": "string",
             "category": "Performance|Security|Availability|Scalability|Usability|Maintainability|Compliance|Observability",
             "description": "string", "targetValue": "string or null", "targetOrigin": "stated|proposed|null",
             "sourceIds": ["NFR-002"],
             "requirementOrigin": "explicit|derived|assumed", "derivationNote": "string or null", "assumedClarifications": ["CQ-003"],
             "evidence": [{"from": "srs|clarification|excerpt", "quote": "string", "sourceId": "NFR-002 or null", "ref": "CQ-003 or notes.pdf#0 or null"}]}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IKnowledgeRetriever _retriever;
    private readonly IAiBatchSettings _batchSettings;
    private readonly ILogger<RequirementGenerationService> _logger;

    internal const int DocumentExcerpts = 4;

    internal const string ObjectivesHeading = "Business objectives of this project:";

    internal static string DocumentContext(IReadOnlyList<RetrievedChunk> chunks) => chunks.Count == 0
        ? string.Empty
        : $"\n\nProject document excerpts (label in brackets):\n{KnowledgeRetriever.FormatExcerpts(chunks)}";

    public RequirementGenerationService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IKnowledgeRetriever retriever, IAiBatchSettings batchSettings, ILogger<RequirementGenerationService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _retriever = retriever;
        _batchSettings = batchSettings;
        _logger = logger;
    }

    public AiChunkRunOperation Operation => AiChunkRunOperation.Generate;

    public async Task<GenerateRequirementsResult> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        await AiChunkRunGuard.EnsureNoActiveRunAsync(_db, _batchSettings, source.Id, Operation, cancellationToken);

        var (inputs, objectives) = await BuildInputsAsync(source, retrieve: true, cancellationToken);

        // A source too large for the request budget of the provider serving the call (Groq) is
        // processed in parts and merged here, never condensed: Gemini gets the whole prompt
        // (source + clarifications + objectives + excerpts) exactly as before. Each part carries the
        // objectives and excerpts, and the clarifications relevant to it.
        var parts = await GenerationSupport.CallAiAndParseChunkedAsync<GenerationAiResponse, GenerationPartAiResponse>(
            _chatClient, _logger, "RequirementGeneration", source.Id.ToString(), SystemPrompt, source.RawText, cancellationToken,
            fixedContext: inputs.FixedContext,
            sharedContext: inputs.Shared,
            knownArtifactCodes: inputs.KnownCodes,
            knownSourceReferences: inputs.KnownReferences,
            validateSourceIds: true);
        var (parsed, rawResponse, model) = Merge(parts, _logger);

        var (result, _) = await PersistAsync(source, objectives, inputs.KnownReferences, parsed, rawResponse, model, onPrepared: null, cancellationToken);

        // P0-7 — over the requirements this run created (result lists only those; skipped duplicates are counted apart).
        return result with
        {
            Completeness = RequirementCompleteness.ForParts(parts, inputs.Shared.Items.Count)
                .WithEvidence(result.FunctionalRequirements.Concat(result.NonFunctionalRequirements))
        };
    }

    // ---- prompt pieces (normal and batched runs) ---------------------------------------------

    // Objectives are previewed, not recorded: they are recorded with the requirements (PersistAsync),
    // so a run that fails records nothing. The codes the model is shown are the ones that preview
    // gives; PersistAsync maps cited codes to objectives by those codes' titles.
    private async Task<(ChunkRunInputs Inputs, List<(string Code, string Title)> Objectives)> BuildInputsAsync(
        RequirementSource source, bool retrieve, CancellationToken cancellationToken)
    {
        var clarifications = await ClarificationContextBuilder.BuildItemsAsync(_db, source.Id, withCodes: true, cancellationToken);

        // §21 — objectives come from the project record, so FRs can be traced back to them.
        var project = await _db.Projects.FirstAsync(p => p.Id == source.ProjectId, cancellationToken);
        var objectives = await BusinessObjectives.PreviewAsync(_db, project.Id, project.Objectives, cancellationToken);
        var objectiveContext = GenerationSupport.BuildContextBlock(ObjectivesHeading, objectives);

        // §26 — ground generation in the project's uploaded documents (meeting notes, specs, ...).
        var excerpts = retrieve
            ? (await _retriever.RetrieveAsync(project.Id, source.RawText, DocumentExcerpts, cancellationToken)).Chunks
            : [];

        var inputs = new ChunkRunInputs(
            PromptTemplateVersion, SystemPrompt, source.RawText,
            objectiveContext + DocumentContext(excerpts),
            new GenerationSupport.SharedContext(ClarificationContextBuilder.Heading, clarifications),
            objectives.Select(o => o.Code).ToList(),
            excerpts.Select(c => c.Reference).ToList(),
            ValidateSourceIds: true,
            ContextMaterial(objectiveContext, clarifications));
        return (inputs, objectives);
    }

    private static string ContextMaterial(string objectiveContext, IReadOnlyList<string> clarifications) =>
        PromptTemplateVersion + "\u001e" + SystemPrompt + "\u001e" + objectiveContext + "\u001e" + string.Concat(clarifications);

    // ---- persistence (normal and batched runs) -----------------------------------------------

    // Everything in one save (ArtifactPersistence): objectives not yet recorded, the requirements
    // that are not already saved for this source (ArtifactFingerprints - a retry or re-run adds
    // nothing twice), their versions and fingerprints, DerivedFrom links to objectives, DependsOn
    // links between requirements (by the source IDs they cite), and the audit rows. A unique-index
    // race (codes or fingerprints taken by a concurrent run) re-prepares against the database.
    //
    // P0-2 — every item's evidence quotes are checked once, before the save (so a save retry reuses
    // the result), against the full stored source, the cited clarification answers and excerpts
    // (RequirementEvidence). The result is stored with the item; it never rejects an item, never asks
    // for a repair, and never changes the model's requirementOrigin. Fingerprints are unchanged.
    private async Task<(GenerateRequirementsResult Result, ChunkRunCommitSummary Summary)> PersistAsync(
        RequirementSource source, IReadOnlyList<(string Code, string Title)> objectivePreview, IEnumerable<string> shownExcerpts,
        GenerationPartAiResponse parsed, string rawResponse, string model,
        Action<ChunkRunCommitSummary>? onPrepared, CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstAsync(p => p.Id == source.ProjectId, cancellationToken);
        var sectionsById = SrsIdExtractor.SectionsById(source.RawText);
        var previewTitles = objectivePreview
            .GroupBy(o => o.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);

        var citedExcerpts = parsed.FunctionalRequirements.SelectMany(i => i.Evidence)
            .Concat(parsed.NonFunctionalRequirements.SelectMany(i => i.Evidence))
            .Where(q => string.Equals(q.From?.Trim(), RequirementEvidence.FromExcerpt, StringComparison.OrdinalIgnoreCase))
            .Select(q => q.Ref ?? string.Empty);
        var evidence = await RequirementEvidence.LoadAsync(_db, source, shownExcerpts, citedExcerpts, cancellationToken);
        var frEvidence = parsed.FunctionalRequirements.ToDictionary(i => i, i => evidence.Assess(i.Evidence), ReferenceEqualityComparer.Instance);
        var nfrEvidence = parsed.NonFunctionalRequirements.ToDictionary(i => i, i => evidence.Assess(i.Evidence), ReferenceEqualityComparer.Instance);

        var frArtifacts = new List<Artifact>();
        var nfrArtifacts = new List<Artifact>();
        var skipped = 0;

        await ArtifactPersistence.SaveAsync(_db, async ct =>
        {
            var now = DateTime.UtcNow;
            frArtifacts = [];
            nfrArtifacts = [];
            skipped = 0;

            var (objectives, _) = await BusinessObjectives.StageAsync(_db, project.Id, project.Objectives, _currentUser.UserId, ct);
            var existing = await ArtifactFingerprints.ExistingAsync(
                _db, source.Id, [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement], ct);

            var newFrs = new List<(FunctionalRequirementItem Item, string Fingerprint)>();
            foreach (var item in parsed.FunctionalRequirements)
            {
                var fingerprint = ArtifactFingerprints.ForFunctional(item.Actor, item.Title, item.Preconditions, item.Inputs, item.Processing, item.ExpectedResult);
                if (existing.Add((ArtifactType.FunctionalRequirement, fingerprint))) newFrs.Add((item, fingerprint));
                else skipped++;
            }

            var newNfrs = new List<(NonFunctionalRequirementItem Item, string Fingerprint)>();
            foreach (var item in parsed.NonFunctionalRequirements)
            {
                var fingerprint = ArtifactFingerprints.ForNonFunctional(item.Category, item.Title, item.Description, item.TargetValue);
                if (existing.Add((ArtifactType.NonFunctionalRequirement, fingerprint))) newNfrs.Add((item, fingerprint));
                else skipped++;
            }

            var frCodes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.FunctionalRequirement, newFrs.Count, ct);
            for (var i = 0; i < newFrs.Count; i++)
            {
                var (item, fingerprint) = newFrs[i];
                var (evidenceStatus, quotes) = frEvidence[item];
                var payload = new FunctionalRequirementPayload
                {
                    Actor = item.Actor,
                    Preconditions = item.Preconditions,
                    Inputs = item.Inputs,
                    Processing = item.Processing,
                    ExpectedResult = item.ExpectedResult,
                    Dependencies = item.Dependencies,
                    SourceReferences = item.SourceReferences,
                    SourceIds = Clean(item.SourceIds),
                    DependsOnSourceIds = Clean(item.DependsOnSourceIds),
                    SourceSection = Section(item.SourceIds, item.PartSection, sectionsById),
                    RequirementOrigin = item.RequirementOrigin,
                    DerivationNote = item.DerivationNote,
                    AssumedClarifications = Clean(item.AssumedClarifications),
                    Evidence = quotes,
                    EvidenceStatus = evidenceStatus,
                    UnsupportedNumbers = RequirementEvidence.UnsupportedNumbers(
                        RequirementEvidence.FunctionalClaim(item.Preconditions, item.Inputs, item.Processing, item.ExpectedResult), quotes),
                    EvidenceCheck = RequirementEvidence.CheckVersion
                };
                var artifact = CreateArtifact(source, ArtifactType.FunctionalRequirement, frCodes[i], item.Title,
                    ParsePriority(item.Priority), JsonSerializer.Serialize(payload), now);
                Add(artifact, fingerprint, now);
                frArtifacts.Add(artifact);
            }

            var nfrCodes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.NonFunctionalRequirement, newNfrs.Count, ct);
            for (var i = 0; i < newNfrs.Count; i++)
            {
                var (item, fingerprint) = newNfrs[i];
                var (evidenceStatus, quotes) = nfrEvidence[item];

                // REAP-011/REAP-045: assumption status is decided here, not trusted from the model -
                // any NFR with a target value is a proposed assumption until a human confirms it,
                // whatever its targetOrigin or TargetSupport says.
                var payload = new NonFunctionalRequirementPayload
                {
                    Category = item.Category,
                    Description = item.Description,
                    TargetValue = item.TargetValue,
                    AssumptionStatus = string.IsNullOrWhiteSpace(item.TargetValue) ? null : "proposed_assumption",
                    SourceIds = Clean(item.SourceIds),
                    SourceSection = Section(item.SourceIds, item.PartSection, sectionsById),
                    RequirementOrigin = item.RequirementOrigin,
                    DerivationNote = item.DerivationNote,
                    AssumedClarifications = Clean(item.AssumedClarifications),
                    Evidence = quotes,
                    EvidenceStatus = evidenceStatus,
                    UnsupportedNumbers = RequirementEvidence.UnsupportedNumbers(
                        RequirementEvidence.NonFunctionalClaim(item.Description, item.TargetValue), quotes),
                    EvidenceCheck = RequirementEvidence.CheckVersion,
                    TargetOrigin = item.TargetOrigin,
                    TargetSupport = RequirementEvidence.TargetSupportFor(item.TargetOrigin, item.TargetValue, quotes)
                };
                var artifact = CreateArtifact(source, ArtifactType.NonFunctionalRequirement, nfrCodes[i], item.Title,
                    priority: null, JsonSerializer.Serialize(payload), now);
                Add(artifact, fingerprint, now);
                nfrArtifacts.Add(artifact);
            }

            // FR -DerivedFrom-> BO: the requirement exists to serve that objective. The model cited
            // the previewed codes; an objective recorded just now is matched by that code's title.
            for (var i = 0; i < newFrs.Count; i++)
            {
                var titles = newFrs[i].Item.BusinessObjectiveCodes
                    .Select(c => previewTitles.GetValueOrDefault(c.Trim()))
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                GenerationSupport.LinkToRelated(_db, frArtifacts[i], objectives.Where(o => titles.Contains(o.Title)),
                    RelationshipType.DerivedFrom, now);
            }

            await LinkDependenciesAsync(source.Id, frArtifacts, newFrs.Select(f => f.Item.DependsOnSourceIds).ToList(), now, ct);

            var created = frArtifacts.Concat(nfrArtifacts).Select(a => a.Id).ToList();
            GenerationSupport.AddExecutions(
                _db, source.ProjectId, "RequirementGeneration", PromptTemplateVersion, _currentUser.UserId,
                model, source.Id.ToString(), rawResponse, now, created);
            onPrepared?.Invoke(new ChunkRunCommitSummary(created, skipped));
        }, cancellationToken);

        if (skipped > 0)
        {
            _logger.LogInformation(
                "RequirementGeneration for source {SourceId}: {Skipped} generated requirements already existed and were not saved again.",
                source.Id, skipped);
        }

        // P0-4 — after the save, against every requirement saved for the source (this run's and
        // earlier ones), so a re-run that only found duplicates reports the same coverage.
        var coverage = await SourceIdCoverage.ForSourceAsync(_db, source, cancellationToken);
        if (coverage.Status == SourceIdCoverage.Incomplete)
        {
            _logger.LogWarning(
                "RequirementGeneration for source {SourceId}: {Uncovered} of {Defined} requirement IDs in the source are cited by no saved requirement ({Ids}).",
                source.Id, coverage.Uncovered.Count, coverage.DefinedCount, string.Join(", ", coverage.Uncovered));
        }

        var result = new GenerateRequirementsResult(
            frArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(),
            nfrArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(),
            skipped,
            coverage);
        return (result, new ChunkRunCommitSummary(frArtifacts.Concat(nfrArtifacts).Select(a => a.Id).ToList(), skipped, coverage));
    }

    private void Add(Artifact artifact, string fingerprint, DateTime now)
    {
        _db.Artifacts.Add(artifact);
        _db.ArtifactVersions.Add(InitialVersion(artifact, now));
        _db.ArtifactFingerprints.Add(ArtifactFingerprints.Record(artifact, fingerprint, now));
    }

    // FR -DependsOn-> FR, for every source ID a new requirement depends on: to the new requirements
    // and the requirements already saved for this source that cite that ID. Never to itself; at
    // most one link per pair.
    private async Task LinkDependenciesAsync(
        Guid sourceId, IReadOnlyList<Artifact> newFrs, IReadOnlyList<List<string>> dependsOn, DateTime now, CancellationToken cancellationToken)
    {
        if (dependsOn.All(d => d.Count == 0))
        {
            return;
        }

        var byId = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        void Index(Guid artifactId, IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id.Trim(), out var set)) byId[id.Trim()] = set = [];
                set.Add(artifactId);
            }
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var saved = await _db.Artifacts.AsNoTracking()
            .Where(a => a.RequirementSourceId == sourceId && a.ArtifactType == ArtifactType.FunctionalRequirement)
            .Select(a => new { a.Id, a.DataJson })
            .ToListAsync(cancellationToken);
        foreach (var artifact in saved)
        {
            var payload = JsonSerializer.Deserialize<FunctionalRequirementPayload>(artifact.DataJson, options);
            Index(artifact.Id, payload?.SourceIds ?? []);
        }

        foreach (var artifact in newFrs)
        {
            var payload = JsonSerializer.Deserialize<FunctionalRequirementPayload>(artifact.DataJson, options);
            Index(artifact.Id, payload?.SourceIds ?? []);
        }

        for (var i = 0; i < newFrs.Count; i++)
        {
            var targets = dependsOn[i]
                .SelectMany(id => byId.GetValueOrDefault(id.Trim()) ?? [])
                .Where(t => t != newFrs[i].Id)
                .Distinct();
            foreach (var target in targets)
            {
                _db.ArtifactRelationships.Add(new ArtifactRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceArtifactId = newFrs[i].Id,
                    TargetArtifactId = target,
                    RelationshipType = RelationshipType.DependsOn,
                    CreatedAt = now
                });
            }
        }
    }

    private static List<string> Clean(IEnumerable<string>? ids) =>
        (ids ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // The section of the first cited source ID; else the section the part started under.
    private static string? Section(IEnumerable<string>? sourceIds, string? partSection, IReadOnlyDictionary<string, string> sectionsById)
    {
        foreach (var id in sourceIds ?? [])
        {
            if (sectionsById.TryGetValue(id.Trim(), out var section)) return Truncate(section);
        }

        return partSection is null ? null : Truncate(partSection);
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200];

    // ---- batched runs (IChunkRunOperation) ---------------------------------------------------

    public async Task<ChunkRunInputs> BuildRunInputsAsync(RequirementSource source, CancellationToken cancellationToken) =>
        (await BuildInputsAsync(source, retrieve: true, cancellationToken)).Inputs;

    public async Task<string> ContextMaterialAsync(RequirementSource source, CancellationToken cancellationToken) =>
        (await BuildInputsAsync(source, retrieve: false, cancellationToken)).Inputs.ContextMaterial;

    public async Task<ChunkRunPartResult> SendPartAsync(ChunkRunPartRequest request, CancellationToken cancellationToken)
    {
        var frozen = request.Frozen;
        var scope = frozen.ValidateSourceIds
            ? new SourceIdScope(request.PartSourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase), frozen.SourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase))
            : null;
        var (raw, parsed) = await GenerationSupport.CallPlannedPartAsync<GenerationAiResponse, GenerationPartAiResponse>(
            _chatClient, _logger, "RequirementGeneration", $"{request.RequirementSourceId}#run-{frozen.RunId}",
            frozen.SystemPrompt, request.Prompt, request.Number, request.Count, request.Delay,
            frozen.KnownCodes, frozen.KnownReferences, scope, cancellationToken);
        return new ChunkRunPartResult(raw, JsonSerializer.Serialize(parsed), _chatClient.ModelName);
    }

    public async Task<ChunkRunCommitSummary> CommitAsync(
        RequirementSource source, ChunkRunFrozen frozen, IReadOnlyList<ChunkRunPartOutput> parts,
        Action<ChunkRunCommitSummary> onPrepared, CancellationToken cancellationToken)
    {
        var aiParts = parts.Select(p => new GenerationSupport.AiPart<GenerationPartAiResponse>(
                p.Number, p.Count, p.RawResponse,
                JsonSerializer.Deserialize<GenerationPartAiResponse>(p.ParsedJson) ?? new GenerationPartAiResponse(),
                p.Model, p.Section))
            .ToList();
        var (parsed, rawResponse, model) = Merge(aiParts, _logger);

        // The caller checked the objectives are unchanged since Start, so this preview gives the
        // codes the parts were shown.
        var project = await _db.Projects.AsNoTracking().FirstAsync(p => p.Id == source.ProjectId, cancellationToken);
        var objectives = await BusinessObjectives.PreviewAsync(_db, project.Id, project.Objectives, cancellationToken);
        var (_, summary) = await PersistAsync(source, objectives, frozen.KnownReferences, parsed, rawResponse, model, onPrepared, cancellationToken);
        return summary;
    }

    private Artifact CreateArtifact(
        RequirementSource source, ArtifactType type, string code, string title,
        ArtifactPriority? priority, string dataJson, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = source.ProjectId,
        ArtifactType = type,
        Code = code,
        Title = title,
        Priority = priority,
        Status = ArtifactStatus.AiGenerated,
        Origin = ArtifactOrigin.Ai,
        RequirementSourceId = source.Id,
        DataJson = dataJson,
        CurrentVersion = 1,
        CreatedByUserId = _currentUser.UserId,
        CreatedAt = now,
        UpdatedAt = now
    };

    private ArtifactVersion InitialVersion(Artifact artifact, DateTime now) => new()
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
        Reason = "AI requirement generation",
        Origin = ArtifactOrigin.Ai
    };

    private static ArtifactPriority ParsePriority(string? priority) =>
        Enum.TryParse<ArtifactPriority>(priority, ignoreCase: true, out var result) ? result : ArtifactPriority.Medium;

    // Parts are merged in source order. Neighbouring parts often restate the same requirement: an
    // FR whose actor, title, preconditions, inputs, processing and expected result all match an
    // earlier one after normalization (an NFR: category, title, description and target value) is
    // merged into it - its objective codes, excerpt references, dependencies and source IDs are
    // added to the first - and the count is logged. Requirements that differ in any of those fields
    // are all kept, even with the same title. The audit keeps every part's raw response and the
    // model(s) that served them. A single whole-source response is returned as it is.
    private static (GenerationPartAiResponse Parsed, string RawResponse, string Model) Merge(
        IReadOnlyList<GenerationSupport.AiPart<GenerationPartAiResponse>> parts, ILogger logger)
    {
        foreach (var part in parts)
        {
            part.Parsed.FunctionalRequirements.ForEach(i => i.PartSection = part.Section);
            part.Parsed.NonFunctionalRequirements.ForEach(i => i.PartSection = part.Section);
        }

        if (parts.Count == 1)
        {
            return (parts[0].Parsed, parts[0].RawResponse, parts[0].Model);
        }

        var merged = new GenerationPartAiResponse();
        var frByKey = new Dictionary<string, FunctionalRequirementItem>(StringComparer.Ordinal);
        var nfrByKey = new Dictionary<string, NonFunctionalRequirementItem>(StringComparer.Ordinal);
        var duplicates = 0;

        foreach (var item in parts.SelectMany(p => p.Parsed.FunctionalRequirements))
        {
            var key = ContentKey(item.Actor, item.Title, item.Preconditions, item.Inputs, item.Processing, item.ExpectedResult);
            if (frByKey.TryGetValue(key, out var first))
            {
                first.BusinessObjectiveCodes = Union(first.BusinessObjectiveCodes, item.BusinessObjectiveCodes);
                first.SourceReferences = Union(first.SourceReferences, item.SourceReferences);
                first.Dependencies = Union(first.Dependencies, item.Dependencies);
                first.SourceIds = Union(first.SourceIds, item.SourceIds);
                first.DependsOnSourceIds = Union(first.DependsOnSourceIds, item.DependsOnSourceIds);
                first.AssumedClarifications = Union(first.AssumedClarifications, item.AssumedClarifications);
                first.Evidence = UnionEvidence(first.Evidence, item.Evidence);
                duplicates++;
                continue;
            }

            frByKey[key] = item;
            merged.FunctionalRequirements.Add(item);
        }

        foreach (var item in parts.SelectMany(p => p.Parsed.NonFunctionalRequirements))
        {
            var key = ContentKey(item.Category, item.Title, item.Description, item.TargetValue);
            if (nfrByKey.TryGetValue(key, out var first))
            {
                first.SourceIds = Union(first.SourceIds, item.SourceIds);
                first.AssumedClarifications = Union(first.AssumedClarifications, item.AssumedClarifications);
                first.Evidence = UnionEvidence(first.Evidence, item.Evidence);
                duplicates++;
                continue;
            }

            nfrByKey[key] = item;
            merged.NonFunctionalRequirements.Add(item);
        }

        if (merged.FunctionalRequirements.Count == 0)
        {
            const string empty = "No functional requirements were produced from any part of the requirement source.";
            throw new AiOutputValidationException(empty, [empty], repairable: false);
        }

        logger.LogInformation(
            "RequirementGeneration merged {Parts} parts: {Frs} functional and {Nfrs} non-functional requirements; {Duplicates} duplicates merged.",
            parts.Count, merged.FunctionalRequirements.Count, merged.NonFunctionalRequirements.Count, duplicates);

        var rawResponse = JsonSerializer.Serialize(new
        {
            parts = parts.Select(p => new { part = p.Number, of = p.Count, model = p.Model, response = p.RawResponse })
        });
        var model = string.Join(" + ", parts.Select(p => p.Model).Distinct());
        return (merged, rawResponse, model);
    }

    // Each field normalized on its own (AiResultMerge.Key) and kept apart, so two requirements are
    // one only when every content field matches - never because their titles do.
    private static string ContentKey(params string?[] fields) =>
        string.Join("\u001f", fields.Select(f => AiResultMerge.Key(f ?? string.Empty)));

    private static List<string> Union(List<string> first, List<string> other) =>
        first.Concat(other).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Both parts' quotes, in document order, without repeats (same source, quote, ID and reference).
    // The first occurrence's requirementOrigin, derivationNote and targetOrigin are kept as they are.
    private static List<EvidenceQuotePayload> UnionEvidence(List<EvidenceQuotePayload> first, List<EvidenceQuotePayload> other) =>
        first.Concat(other)
            .DistinctBy(q => string.Join("\u001f",
                q.From?.Trim().ToLowerInvariant(), SourceEvidence.Normalize(q.Quote), q.SourceId?.Trim().ToUpperInvariant(), q.Ref?.Trim().ToUpperInvariant()))
            .ToList();

    // One part of a split source: it may legitimately state no functional requirement (background
    // prose, a glossary), so "at least one" is judged on the merged result instead (Merge).
    private class GenerationPartAiResponse : IValidatableAiResponse
    {
        protected virtual int MinFunctionalRequirements => 0;

        public List<FunctionalRequirementItem> FunctionalRequirements { get; set; } = new();
        public List<NonFunctionalRequirementItem> NonFunctionalRequirements { get; set; } = new();

        public void Validate(AiResponseValidator v)
        {
            v.Items(FunctionalRequirements, "functionalRequirements", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.Required(item.Actor, $"{path}.actor");
                v.OneOf(item.Priority, $"{path}.priority", AiVocabulary.Priorities, optional: true);
                v.CodesExist(item.BusinessObjectiveCodes, $"{path}.businessObjectiveCodes");
                v.SourceReferencesExist(item.SourceReferences, $"{path}.sourceReferences");
                v.SourceIdsExist(item.SourceIds, $"{path}.sourceIds");
                v.SourceIdsExist(item.DependsOnSourceIds, $"{path}.dependsOnSourceIds", anywhereInSource: true);
            }, minItems: MinFunctionalRequirements);
            v.Items(NonFunctionalRequirements, "nonFunctionalRequirements", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.OneOf(item.Category, $"{path}.category", AiVocabulary.NfrCategories);
                v.SourceIdsExist(item.SourceIds, $"{path}.sourceIds");
            });
        }
    }

    // A response for the whole source must produce at least one functional requirement.
    private class GenerationAiResponse : GenerationPartAiResponse
    {
        protected override int MinFunctionalRequirements => 1;
    }

    private class FunctionalRequirementItem
    {
        public string Title { get; set; } = string.Empty;
        public string Actor { get; set; } = string.Empty;
        public string? Priority { get; set; }
        public string? Preconditions { get; set; }
        public string? Inputs { get; set; }
        public string? Processing { get; set; }
        public string? ExpectedResult { get; set; }
        public List<string> Dependencies { get; set; } = new();
        public List<string> BusinessObjectiveCodes { get; set; } = new();
        public List<string> SourceReferences { get; set; } = new();
        public List<string> SourceIds { get; set; } = new();
        public List<string> DependsOnSourceIds { get; set; } = new();

        // P0-2 — read leniently (RequirementEvidence): never a reason to reject or repair a response.
        [JsonConverter(typeof(LenientStringConverter))]
        public string? RequirementOrigin { get; set; }

        [JsonConverter(typeof(LenientStringConverter))]
        public string? DerivationNote { get; set; }

        [JsonConverter(typeof(LenientStringListConverter))]
        public List<string> AssumedClarifications { get; set; } = new();

        [JsonConverter(typeof(LenientEvidenceListConverter))]
        public List<EvidenceQuotePayload> Evidence { get; set; } = new();

        // The section the part it came from started under; not part of the AI response.
        [JsonIgnore]
        public string? PartSection { get; set; }
    }

    private class NonFunctionalRequirementItem
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? TargetValue { get; set; }
        public List<string> SourceIds { get; set; } = new();

        [JsonConverter(typeof(LenientStringConverter))]
        public string? TargetOrigin { get; set; }

        [JsonConverter(typeof(LenientStringConverter))]
        public string? RequirementOrigin { get; set; }

        [JsonConverter(typeof(LenientStringConverter))]
        public string? DerivationNote { get; set; }

        [JsonConverter(typeof(LenientStringListConverter))]
        public List<string> AssumedClarifications { get; set; } = new();

        [JsonConverter(typeof(LenientEvidenceListConverter))]
        public List<EvidenceQuotePayload> Evidence { get; set; } = new();

        [JsonIgnore]
        public string? PartSection { get; set; }
    }
}
