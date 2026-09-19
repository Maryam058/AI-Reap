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

public class UserStoryService : IUserStoryService
{
    private const string StorySystemPrompt = """
        You are a requirements engineer (§12 of an SDLC automation spec). Given a raw
        requirement, its clarifications, and the functional requirements already derived from
        it, write user stories. Reference the functional requirement(s) each story realizes by
        their code (e.g. "FR-001") in relatedRequirementCodes - only use codes that were given
        to you, never invent one.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "userStories": [
            {"title": "string", "persona": "string", "valueStatement": "string",
             "priority": "Low|Medium|High|Critical", "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private const string AcceptanceCriteriaSystemPrompt = """
        You are a QA-minded requirements engineer (§13 of an SDLC automation spec). Given a
        user story, write acceptance criteria in Given/When/Then form, covering positive,
        negative, and boundary cases.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "acceptanceCriteria": [
            {"title": "string", "given": "string", "when": "string", "then": "string",
             "kind": "positive|negative|boundary"}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public UserStoryService(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> GenerateStoriesAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
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

        var rawResponse = await _chatClient.CompleteAsync(StorySystemPrompt, userPrompt, cancellationToken);
        var parsed = AiJsonParser.Parse<StoryAiResponse>(rawResponse);

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, source.ProjectId, ArtifactType.UserStory, parsed.UserStories.Count, cancellationToken);

        var storyArtifacts = new List<Artifact>();
        var relationships = new List<ArtifactRelationship>();

        for (var i = 0; i < parsed.UserStories.Count; i++)
        {
            var item = parsed.UserStories[i];
            var payload = new UserStoryPayload { Persona = item.Persona, ValueStatement = item.ValueStatement };

            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = source.ProjectId,
                ArtifactType = ArtifactType.UserStory,
                Code = codes[i],
                Title = item.Title,
                Priority = Enum.TryParse<ArtifactPriority>(item.Priority, true, out var priority) ? priority : ArtifactPriority.Medium,
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
                Reason = "AI user story generation",
                Origin = ArtifactOrigin.Ai
            });

            foreach (var frCode in item.RelatedRequirementCodes)
            {
                var relatedFr = functionalRequirements.FirstOrDefault(fr => fr.Code == frCode);
                if (relatedFr is not null)
                {
                    relationships.Add(new ArtifactRelationship
                    {
                        Id = Guid.NewGuid(),
                        SourceArtifactId = artifact.Id,
                        TargetArtifactId = relatedFr.Id,
                        RelationshipType = RelationshipType.DerivedFrom,
                        CreatedAt = now
                    });
                }
            }

            storyArtifacts.Add(artifact);
        }

        foreach (var relationship in relationships)
        {
            _db.ArtifactRelationships.Add(relationship);
        }

        _db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            OperationType = "UserStoryGeneration",
            UserId = _currentUser.UserId,
            Timestamp = now,
            Model = _chatClient.ModelName,
            InputReference = source.Id.ToString(),
            OutputJson = rawResponse,
            Accepted = null
        });

        await _db.SaveChangesAsync(cancellationToken);

        return storyArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    public async Task<IReadOnlyList<ArtifactResponse>?> GenerateAcceptanceCriteriaAsync(Guid userStoryArtifactId, CancellationToken cancellationToken = default)
    {
        var story = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == userStoryArtifactId, cancellationToken);
        if (story is null || story.ArtifactType != ArtifactType.UserStory)
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<UserStoryPayload>(
            story.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new UserStoryPayload();

        var userPrompt =
            $"Title: {story.Title}\nPersona: {payload.Persona}\nValue: {payload.ValueStatement}";

        var rawResponse = await _chatClient.CompleteAsync(AcceptanceCriteriaSystemPrompt, userPrompt, cancellationToken);
        var parsed = AiJsonParser.Parse<AcceptanceCriteriaAiResponse>(rawResponse);

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, story.ProjectId, ArtifactType.AcceptanceCriterion, parsed.AcceptanceCriteria.Count, cancellationToken);

        var acArtifacts = new List<Artifact>();

        for (var i = 0; i < parsed.AcceptanceCriteria.Count; i++)
        {
            var item = parsed.AcceptanceCriteria[i];
            var acPayload = new AcceptanceCriterionPayload
            {
                Given = item.Given,
                When = item.When,
                Then = item.Then,
                Kind = item.Kind
            };

            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = story.ProjectId,
                ArtifactType = ArtifactType.AcceptanceCriterion,
                Code = codes[i],
                Title = item.Title,
                Status = ArtifactStatus.AiGenerated,
                Origin = ArtifactOrigin.Ai,
                RequirementSourceId = story.RequirementSourceId,
                DataJson = JsonSerializer.Serialize(acPayload),
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
                Reason = "AI acceptance criteria generation",
                Origin = ArtifactOrigin.Ai
            });

            _db.ArtifactRelationships.Add(new ArtifactRelationship
            {
                Id = Guid.NewGuid(),
                SourceArtifactId = artifact.Id,
                TargetArtifactId = story.Id,
                RelationshipType = RelationshipType.DerivedFrom,
                CreatedAt = now
            });

            acArtifacts.Add(artifact);
        }

        _db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = story.ProjectId,
            OperationType = "AcceptanceCriteriaGeneration",
            UserId = _currentUser.UserId,
            Timestamp = now,
            Model = _chatClient.ModelName,
            InputReference = story.Id.ToString(),
            OutputJson = rawResponse,
            Accepted = null
        });

        await _db.SaveChangesAsync(cancellationToken);

        return acArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    private class StoryAiResponse
    {
        public List<UserStoryItem> UserStories { get; set; } = new();
    }

    private class UserStoryItem
    {
        public string Title { get; set; } = string.Empty;
        public string Persona { get; set; } = string.Empty;
        public string ValueStatement { get; set; } = string.Empty;
        public string? Priority { get; set; }
        public List<string> RelatedRequirementCodes { get; set; } = new();
    }

    private class AcceptanceCriteriaAiResponse
    {
        public List<AcceptanceCriterionItem> AcceptanceCriteria { get; set; } = new();
    }

    private class AcceptanceCriterionItem
    {
        public string Title { get; set; } = string.Empty;
        public string Given { get; set; } = string.Empty;
        public string When { get; set; } = string.Empty;
        public string Then { get; set; } = string.Empty;
        public string Kind { get; set; } = "positive";
    }
}
