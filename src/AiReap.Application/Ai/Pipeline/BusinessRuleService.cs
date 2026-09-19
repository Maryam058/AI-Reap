using System.Text;
using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

public class BusinessRuleService : IBusinessRuleService
{
    private const string SystemPrompt = """
        You are a requirements engineer (§11 of an SDLC automation spec). Given a raw
        requirement, its clarifications, and the functional requirements already derived from
        it, extract the business rules implied or stated - constraints on data, behavior, or
        process that the system must enforce (e.g. "Requests exceeding 10 days require
        second-level approval"). Reference the functional requirement(s) each rule constrains
        by code (e.g. "FR-001") in relatedRequirementCodes - only use codes given to you, never
        invent one. Skip anything already fully captured as a plain FR field; only extract
        rules that read as a constraint/policy statement.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "businessRules": [
            {"title": "string", "statement": "string", "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public BusinessRuleService(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");

        var functionalRequirements = await _db.Artifacts
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == ArtifactType.FunctionalRequirement)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = new StringBuilder("\n\nFunctional requirements derived so far:\n");
        foreach (var fr in functionalRequirements)
        {
            frContext.Append($"- {fr.Code}: {fr.Title}\n");
        }

        var userPrompt = source.RawText + context + (functionalRequirements.Count > 0 ? frContext.ToString() : string.Empty);

        var rawResponse = await _chatClient.CompleteAsync(SystemPrompt, userPrompt, cancellationToken);
        var parsed = AiJsonParser.Parse<BusinessRuleAiResponse>(rawResponse);

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, source.ProjectId, ArtifactType.BusinessRule, parsed.BusinessRules.Count, cancellationToken);

        var ruleArtifacts = new List<Artifact>();

        for (var i = 0; i < parsed.BusinessRules.Count; i++)
        {
            var item = parsed.BusinessRules[i];
            var payload = new BusinessRulePayload { Statement = item.Statement };

            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = source.ProjectId,
                ArtifactType = ArtifactType.BusinessRule,
                Code = codes[i],
                Title = item.Title,
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
                Reason = "AI business rule extraction",
                Origin = ArtifactOrigin.Ai
            });

            foreach (var frCode in item.RelatedRequirementCodes)
            {
                var relatedFr = functionalRequirements.FirstOrDefault(fr => fr.Code == frCode);
                if (relatedFr is not null)
                {
                    _db.ArtifactRelationships.Add(new ArtifactRelationship
                    {
                        Id = Guid.NewGuid(),
                        SourceArtifactId = artifact.Id,
                        TargetArtifactId = relatedFr.Id,
                        RelationshipType = RelationshipType.LinkedRule,
                        CreatedAt = now
                    });
                }
            }

            ruleArtifacts.Add(artifact);
        }

        _db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            OperationType = "BusinessRuleExtraction",
            UserId = _currentUser.UserId,
            Timestamp = now,
            Model = _chatClient.ModelName,
            InputReference = source.Id.ToString(),
            OutputJson = rawResponse,
            Accepted = null
        });

        await _db.SaveChangesAsync(cancellationToken);

        return ruleArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    private class BusinessRuleAiResponse
    {
        public List<BusinessRuleItem> BusinessRules { get; set; } = new();
    }

    private class BusinessRuleItem
    {
        public string Title { get; set; } = string.Empty;
        public string Statement { get; set; } = string.Empty;
        public List<string> RelatedRequirementCodes { get; set; } = new();
    }
}
