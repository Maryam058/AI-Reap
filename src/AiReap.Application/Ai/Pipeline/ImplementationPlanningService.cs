using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public class ImplementationPlanningService : IImplementationPlanningService
{
    private const string PromptTemplateVersion = "ImplementationPlanning-v1";

    private const string SystemPrompt = """
        You are a delivery lead (§19 of an SDLC automation spec). Given a raw requirement, its
        clarifications, and the functional requirements (plus any database entities and API
        endpoints) derived from it, break the work into concrete implementation tasks (e.g.
        "Create LeaveRequest database entity", "Create POST /api/leave-requests", "Create React
        leave request form"). Reference the functional requirement(s) each task realizes by
        code (e.g. "FR-001") in relatedRequirementCodes - only use codes given to you, never
        invent one.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "tasks": [
            {"title": "string", "description": "string",
             "taskType": "Backend|Frontend|Database|Infrastructure|Testing",
             "priority": "Low|Medium|High|Critical", "dependencies": ["string"],
             "suggestedRole": "string or null", "estimate": "string or null",
             "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<ImplementationPlanningService> _logger;

    public ImplementationPlanningService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<ImplementationPlanningService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await GenerationSupport.LoadSourceAsync(_db, requirementSourceId, cancellationToken);
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        var functionalRequirements = await GenerationSupport.LoadArtifactsAsync(_db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        var dataEntities = await GenerationSupport.LoadArtifactsAsync(_db, requirementSourceId, ArtifactType.DataEntity, cancellationToken);
        var apiSpecs = await GenerationSupport.LoadArtifactsAsync(_db, requirementSourceId, ArtifactType.ApiSpecification, cancellationToken);

        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildContextBlock("Functional requirements derived so far:", functionalRequirements);
        var dataContext = GenerationSupport.BuildContextBlock("Data entities proposed so far:", dataEntities);
        var apiContext = GenerationSupport.BuildContextBlock("API endpoints proposed so far:", apiSpecs);

        var userPrompt = source.RawText + context + frContext + dataContext + apiContext;
        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<TaskAiResponse>(
            _chatClient, _logger, "ImplementationPlanning", source.Id.ToString(), SystemPrompt, userPrompt, cancellationToken);

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.ImplementationTask, parsed.Tasks.Count, cancellationToken);

        var taskArtifacts = new List<Artifact>();

        for (var i = 0; i < parsed.Tasks.Count; i++)
        {
            var item = parsed.Tasks[i];
            var payload = new ImplementationTaskPayload
            {
                Description = item.Description,
                TaskType = item.TaskType,
                Dependencies = item.Dependencies,
                SuggestedRole = item.SuggestedRole,
                Estimate = item.Estimate
            };

            var priority = Enum.TryParse<ArtifactPriority>(item.Priority, true, out var p) ? p : ArtifactPriority.Medium;

            var artifact = GenerationSupport.NewArtifact(
                source, ArtifactType.ImplementationTask, codes[i], item.Title, priority, JsonSerializer.Serialize(payload), _currentUser.UserId, now);

            _db.Artifacts.Add(artifact);
            _db.ArtifactVersions.Add(GenerationSupport.InitialVersion(artifact, "AI implementation planning", _currentUser.UserId, now));

            var related = functionalRequirements.Where(fr => item.RelatedRequirementCodes.Contains(fr.Code));
            GenerationSupport.LinkToRelated(_db, artifact, related, RelationshipType.Implements, now);

            taskArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "ImplementationPlanning", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            taskArtifacts.Select(a => a.Id).ToList());

        await _db.SaveChangesAsync(cancellationToken);

        return taskArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    private class TaskAiResponse
    {
        public List<TaskItem> Tasks { get; set; } = new();
    }

    private class TaskItem
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string TaskType { get; set; } = string.Empty;
        public string? Priority { get; set; }
        public List<string> Dependencies { get; set; } = new();
        public string? SuggestedRole { get; set; }
        public string? Estimate { get; set; }
        public List<string> RelatedRequirementCodes { get; set; } = new();
    }
}
