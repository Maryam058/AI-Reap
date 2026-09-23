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

public class RequirementGenerationService : IRequirementGenerationService
{
    private const string PromptTemplateVersion = "RequirementGeneration-v1";

    private const string SystemPrompt = """
        You are a requirements engineer (§9/§10 of an SDLC automation spec). Given a raw
        requirement and any clarifications gathered so far, produce structured functional and
        non-functional requirements.

        For non-functional requirements: if you propose a numeric or measurable target that was
        not explicitly stated by a human, you MUST still include it (as targetValue) but mark
        assumptionStatus as "proposed_assumption" - never claim a value is "confirmed" unless a
        human explicitly stated that exact number in the input or clarifications.

        Respond with ONLY a single JSON object, no markdown fences, no commentary, matching
        exactly this shape:
        {
          "functionalRequirements": [
            {"title": "string", "actor": "string", "priority": "Low|Medium|High|Critical",
             "preconditions": "string", "inputs": "string", "processing": "string",
             "expectedResult": "string", "dependencies": ["string"]}
          ],
          "nonFunctionalRequirements": [
            {"title": "string",
             "category": "Performance|Security|Availability|Scalability|Usability|Maintainability|Compliance|Observability",
             "description": "string", "targetValue": "string or null",
             "assumptionStatus": "confirmed|proposed_assumption"}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<RequirementGenerationService> _logger;

    public RequirementGenerationService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<RequirementGenerationService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _logger = logger;
    }

    public async Task<GenerateRequirementsResult> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);

        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var userPrompt = source.RawText + context;

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<GenerationAiResponse>(
            _chatClient, _logger, "RequirementGeneration", source.Id.ToString(), SystemPrompt, userPrompt, cancellationToken);

        var now = DateTime.UtcNow;

        var frCodes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, source.ProjectId, ArtifactType.FunctionalRequirement, parsed.FunctionalRequirements.Count, cancellationToken);
        var frArtifacts = new List<Artifact>();
        for (var i = 0; i < parsed.FunctionalRequirements.Count; i++)
        {
            var item = parsed.FunctionalRequirements[i];
            var payload = new FunctionalRequirementPayload
            {
                Actor = item.Actor,
                Preconditions = item.Preconditions,
                Inputs = item.Inputs,
                Processing = item.Processing,
                ExpectedResult = item.ExpectedResult,
                Dependencies = item.Dependencies
            };

            frArtifacts.Add(CreateArtifact(
                source, ArtifactType.FunctionalRequirement, frCodes[i], item.Title,
                ParsePriority(item.Priority), JsonSerializer.Serialize(payload), now));
        }

        var nfrCodes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, source.ProjectId, ArtifactType.NonFunctionalRequirement, parsed.NonFunctionalRequirements.Count, cancellationToken);
        var nfrArtifacts = new List<Artifact>();
        for (var i = 0; i < parsed.NonFunctionalRequirements.Count; i++)
        {
            var item = parsed.NonFunctionalRequirements[i];

            // REAP-011/REAP-045: assumption status is decided here, not trusted from the model -
            // any NFR with a target value is a proposed assumption until a human confirms it.
            var assumptionStatus = string.IsNullOrWhiteSpace(item.TargetValue) ? null : "proposed_assumption";

            var payload = new NonFunctionalRequirementPayload
            {
                Category = item.Category,
                Description = item.Description,
                TargetValue = item.TargetValue,
                AssumptionStatus = assumptionStatus
            };

            nfrArtifacts.Add(CreateArtifact(
                source, ArtifactType.NonFunctionalRequirement, nfrCodes[i], item.Title,
                priority: null, JsonSerializer.Serialize(payload), now));
        }

        foreach (var artifact in frArtifacts.Concat(nfrArtifacts))
        {
            _db.Artifacts.Add(artifact);
            _db.ArtifactVersions.Add(InitialVersion(artifact, now));
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "RequirementGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            frArtifacts.Concat(nfrArtifacts).Select(a => a.Id).ToList());

        await _db.SaveChangesAsync(cancellationToken);

        return new GenerateRequirementsResult(
            frArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(),
            nfrArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList());
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
        ChangedByUserId = _currentUser.UserId,
        ChangedAt = now,
        Reason = "AI requirement generation",
        Origin = ArtifactOrigin.Ai
    };

    private static ArtifactPriority ParsePriority(string? priority) =>
        Enum.TryParse<ArtifactPriority>(priority, ignoreCase: true, out var result) ? result : ArtifactPriority.Medium;

    private class GenerationAiResponse
    {
        public List<FunctionalRequirementItem> FunctionalRequirements { get; set; } = new();
        public List<NonFunctionalRequirementItem> NonFunctionalRequirements { get; set; } = new();
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
    }

    private class NonFunctionalRequirementItem
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? TargetValue { get; set; }
    }
}
