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
    private const string PromptTemplateVersion = "ImplementationPlanning-v4";
    // ImplementationPlanning-v4 (A4b Phase 4) = ImplementationPlanning-v3 plus each listed FR's source IDs (BuildFunctionalRequirementBlock)
    // and the rule that raw-text IDs are document IDs, not citation codes; an FR without source IDs keeps its line.

    private const string SystemPrompt = """
        You are a delivery lead (§19 of an SDLC automation spec). Given a raw requirement, its
        clarifications, and the functional requirements (plus any database entities and API
        endpoints) derived from it, break the work into concrete implementation tasks (e.g.
        "Create LeaveRequest database entity", "Create POST /api/leave-requests", "Create React
        leave request form"). Reference the functional requirement(s) each task realizes by
        code (e.g. "FR-001") in relatedRequirementCodes - only use codes given to you, never
        invent one.
        When functional requirements are listed, every task must cite at least one of
        them - the one(s) it actually serves; never cite an unrelated code just to fill the
        field. When none are listed, leave relatedRequirementCodes empty.
        Cite a requirement only by its stored code - the code on the left of a line in the
        functional requirements list. Identifiers written in the raw requirement text (such as
        "FR-012") are document IDs, not citation codes: one can look like, or even equal, a listed
        code yet name a different requirement. Where a listed line shows "(source IDs: ...)", those
        are the document IDs that requirement came from - cite it by the stored code on the left.
        Never invent a code or cite an unrelated one.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "tasks": [
            {"title": "string", "description": "string",
             "taskType": "Backend|Frontend|Database|Infrastructure|Testing|Documentation",
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
        await GenerationInputs.EnsureNotAllRejectedAsync(_db, requirementSourceId, "plan implementation tasks for", cancellationToken);
        var functionalRequirements = await GenerationSupport.LoadGenerationInputsAsync(_db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        var dataEntities = await GenerationSupport.LoadGenerationInputsAsync(_db, requirementSourceId, ArtifactType.DataEntity, cancellationToken);
        var apiSpecs = await GenerationSupport.LoadGenerationInputsAsync(_db, requirementSourceId, ArtifactType.ApiSpecification, cancellationToken);

        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildFunctionalRequirementBlock(functionalRequirements);
        var dataContext = GenerationSupport.BuildContextBlock("Data entities proposed so far:", dataEntities);
        var apiContext = GenerationSupport.BuildContextBlock("API endpoints proposed so far:", apiSpecs);

        var userPrompt = source.RawText + context + frContext + dataContext + apiContext;
        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<TaskAiResponse>(
            _chatClient, _logger, "ImplementationPlanning", source.Id.ToString(), SystemPrompt, userPrompt, cancellationToken,
            // Data entities and API endpoints are context only: relatedRequirementCodes may cite FRs alone.
            knownArtifactCodes: functionalRequirements.Select(fr => fr.Code));

        // G2 — one save (ArtifactPersistence) of the implementation tasks not already saved for this source
        // (ArtifactFingerprints): a re-run with the same output adds nothing, and existing artifacts are
        // never changed. A unique-index race re-prepares against the database.
        var taskArtifacts = new List<Artifact>();
        var skipped = 0;
        await ArtifactPersistence.SaveAsync(_db, async ct =>
            (taskArtifacts, skipped) = await PrepareTasksAsync(source, parsed, functionalRequirements, rawResponse, ct), cancellationToken);

        if (skipped > 0)
        {
            _logger.LogInformation(
                "{Operation} for {Input}: {Created} created, {Skipped} already existed and were not saved again.",
                "ImplementationPlanning", source.Id, taskArtifacts.Count, skipped);
        }

        return new GeneratedArtifacts(taskArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(), skipped);
    }

    // The implementation tasks of the response not already saved for the source, added to the context with
    // their versions, fingerprints, links and audit rows - not saved here.
    private async Task<(List<Artifact> Created, int Skipped)> PrepareTasksAsync(
        RequirementSource source, TaskAiResponse parsed, IReadOnlyList<Artifact> functionalRequirements, string rawResponse,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var existing = await ArtifactFingerprints.ExistingAsync(_db, source.Id, [ArtifactType.ImplementationTask], cancellationToken);
        var fresh = ArtifactFingerprints.Fresh(parsed.Tasks, ArtifactType.ImplementationTask,
            t => ArtifactFingerprints.ForImplementationTask(t.Title, t.TaskType, t.Description), existing, out var skipped);
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.ImplementationTask, fresh.Count, cancellationToken);

        var taskArtifacts = new List<Artifact>();

        for (var i = 0; i < fresh.Count; i++)
        {
            var item = fresh[i].Item;
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
            _db.ArtifactFingerprints.Add(ArtifactFingerprints.Record(artifact, fresh[i].Fingerprint, now));

            var related = GenerationSupport.ResolveCited(item.RelatedRequirementCodes, functionalRequirements);
            GenerationSupport.LinkToRelated(_db, artifact, related, RelationshipType.Implements, now);

            taskArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "ImplementationPlanning", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            taskArtifacts.Select(a => a.Id).ToList());

        return (taskArtifacts, skipped);
    }

    private class TaskAiResponse : IValidatableAiResponse
    {
        public List<TaskItem> Tasks { get; set; } = new();

        public void Validate(AiResponseValidator v) =>
            v.Items(Tasks, "tasks", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.Required(item.Description, $"{path}.description");
                v.OneOf(item.TaskType, $"{path}.taskType", AiVocabulary.TaskTypes);
                v.OneOf(item.Priority, $"{path}.priority", AiVocabulary.Priorities, optional: true);
                v.CodesExist(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
                v.CodesRequired(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
            }, minItems: 1);
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
