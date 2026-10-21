using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public class RequirementAnalysisService : IRequirementAnalysisService, IChunkRunOperation
{
    internal const string PromptTemplateVersion = "RequirementAnalysis-v1";

    // Shared with the dry-run AI plan (AiPlanService), so it plans the prompt a real run sends.
    internal const string SystemPrompt = """
        You are a requirements analyst (§7 of an SDLC automation spec). Given a raw, possibly
        incomplete software requirement, decompose it into actors, capabilities, data elements
        the system must track, informal notes/rules you observed, and a list of missing
        information that would need to be clarified before this could become a confirmed
        requirement. Never invent a confirmed detail (a number, a rule, a permission) that was
        not stated - if it is unclear or unstated, put it under missingInformation instead.

        Respond with ONLY a single JSON object, no markdown fences, no commentary, matching
        exactly this shape:
        {
          "actors": ["string"],
          "capabilities": ["string"],
          "dataElements": ["string"],
          "notes": ["string"],
          "missingInformation": [{"topic": "string", "question": "string", "reason": "string"}]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IAiBatchSettings _batchSettings;
    private readonly ILogger<RequirementAnalysisService> _logger;

    public RequirementAnalysisService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IAiBatchSettings batchSettings, ILogger<RequirementAnalysisService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _batchSettings = batchSettings;
        _logger = logger;
    }

    public AiChunkRunOperation Operation => AiChunkRunOperation.Analyze;

    public async Task<AnalyzeRequirementResult> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        await AiChunkRunGuard.EnsureNoActiveRunAsync(_db, _batchSettings, source.Id, Operation, cancellationToken);

        // A source too large for the request budget of the provider serving the call (Groq) is
        // analysed in parts and merged here, never condensed; Gemini gets the whole source as before.
        var parts = await GenerationSupport.CallAiAndParseChunkedAsync<AnalysisAiResponse, AnalysisPartAiResponse>(
            _chatClient, _logger, "RequirementAnalysis", source.Id.ToString(), SystemPrompt, source.RawText, cancellationToken);
        var (parsed, rawResponse, model) = parts.Count == 1
            ? (parts[0].Parsed, parts[0].RawResponse, parts[0].Model)
            : Merge(parts);

        var (questions, skipped) = await PersistAsync(source, parsed, rawResponse, model, onPrepared: null, cancellationToken);

        return new AnalyzeRequirementResult(
            parsed.Actors,
            parsed.Capabilities,
            parsed.DataElements,
            parsed.Notes,
            questions.Select(ArtifactResponseMapper.ToResponse).ToList(),
            skipped,
            RequirementCompleteness.ForParts(parts, clarificationCount: 0));
    }

    // One save (ArtifactPersistence): the questions not already saved for this source
    // (ArtifactFingerprints - a re-run or retry adds no duplicate), their versions and fingerprints,
    // and the audit rows. A unique-index race re-prepares against the database.
    private async Task<(List<Artifact> Questions, int Skipped)> PersistAsync(
        RequirementSource source, AnalysisPartAiResponse parsed, string rawResponse, string model,
        Action<ChunkRunCommitSummary>? onPrepared, CancellationToken cancellationToken)
    {
        var questionArtifacts = new List<Artifact>();
        var skipped = 0;

        await ArtifactPersistence.SaveAsync(_db, async ct =>
        {
            var now = DateTime.UtcNow;
            questionArtifacts = [];
            skipped = 0;

            var existing = await ArtifactFingerprints.ExistingAsync(_db, source.Id, [ArtifactType.ClarificationQuestion], ct);
            var fresh = new List<(MissingInformationItem Item, string Fingerprint)>();
            foreach (var item in parsed.MissingInformation)
            {
                var fingerprint = ArtifactFingerprints.ForClarification(item.Question);
                if (existing.Add((ArtifactType.ClarificationQuestion, fingerprint))) fresh.Add((item, fingerprint));
                else skipped++;
            }

            var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
                _db, source.ProjectId, ArtifactType.ClarificationQuestion, fresh.Count, ct);

            for (var i = 0; i < fresh.Count; i++)
            {
                var (item, fingerprint) = fresh[i];
                var payload = new ClarificationQuestionPayload
                {
                    Question = item.Question,
                    Reason = item.Reason,
                    ClarificationStatus = "Open"
                };

                var artifact = new Artifact
                {
                    Id = Guid.NewGuid(),
                    ProjectId = source.ProjectId,
                    ArtifactType = ArtifactType.ClarificationQuestion,
                    Code = codes[i],
                    Title = item.Topic,
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
            GenerationSupport.AddExecutions(
                _db, source.ProjectId, "RequirementAnalysis", PromptTemplateVersion, _currentUser.UserId,
                model, source.Id.ToString(), rawResponse, now, created);
            onPrepared?.Invoke(new ChunkRunCommitSummary(created, skipped));
        }, cancellationToken);

        if (skipped > 0)
        {
            _logger.LogInformation(
                "RequirementAnalysis for source {SourceId}: {Skipped} clarification questions already existed and were not saved again.",
                source.Id, skipped);
        }

        return (questionArtifacts, skipped);
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
        var (questions, skipped) = await PersistAsync(source, parsed, rawResponse, model, onPrepared, cancellationToken);
        return new ChunkRunCommitSummary(questions.Select(q => q.Id).ToList(), skipped);
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
        public string Topic { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
