using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

public class RequirementAnalysisService : IRequirementAnalysisService
{
    private const string SystemPrompt = """
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

    public RequirementAnalysisService(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<AnalyzeRequirementResult> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");

        var rawResponse = await _chatClient.CompleteAsync(SystemPrompt, source.RawText, cancellationToken);
        var parsed = AiJsonParser.Parse<AnalysisAiResponse>(rawResponse);

        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, source.ProjectId, ArtifactType.ClarificationQuestion, parsed.MissingInformation.Count, cancellationToken);

        var now = DateTime.UtcNow;
        var questionArtifacts = new List<Artifact>();

        for (var i = 0; i < parsed.MissingInformation.Count; i++)
        {
            var item = parsed.MissingInformation[i];
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
                ChangedByUserId = _currentUser.UserId,
                ChangedAt = now,
                Reason = "AI requirement analysis",
                Origin = ArtifactOrigin.Ai
            });

            questionArtifacts.Add(artifact);
        }

        _db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            OperationType = "RequirementAnalysis",
            UserId = _currentUser.UserId,
            Timestamp = now,
            Model = _chatClient.ModelName,
            InputReference = source.Id.ToString(),
            OutputJson = rawResponse,
            Accepted = null
        });

        await _db.SaveChangesAsync(cancellationToken);

        return new AnalyzeRequirementResult(
            parsed.Actors,
            parsed.Capabilities,
            parsed.DataElements,
            parsed.Notes,
            questionArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList());
    }

    private class AnalysisAiResponse
    {
        public List<string> Actors { get; set; } = new();
        public List<string> Capabilities { get; set; } = new();
        public List<string> DataElements { get; set; } = new();
        public List<string> Notes { get; set; } = new();
        public List<MissingInformationItem> MissingInformation { get; set; } = new();
    }

    private class MissingInformationItem
    {
        public string Topic { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
