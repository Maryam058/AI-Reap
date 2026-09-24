using System.Text.Json;
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

public class RequirementGenerationService : IRequirementGenerationService
{
    private const string PromptTemplateVersion = "RequirementGeneration-v2";

    private const string SystemPrompt = """
        You are a requirements engineer (§9/§10 of an SDLC automation spec). Given a raw
        requirement and any clarifications gathered so far, produce structured functional and
        non-functional requirements.

        For non-functional requirements: if you propose a numeric or measurable target that was
        not explicitly stated by a human, you MUST still include it (as targetValue) but mark
        assumptionStatus as "proposed_assumption" - never claim a value is "confirmed" unless a
        human explicitly stated that exact number in the input or clarifications.

        If project document excerpts are provided, use them only to inform requirements that the
        raw requirement or clarifications support, and list the labels of the excerpts a
        requirement relies on in sourceReferences (e.g. "notes.pdf#3"); use [] otherwise.

        If business objectives are listed, set businessObjectiveCodes on each functional
        requirement to the codes (e.g. "BO-001") of the objectives it directly serves. Use only
        codes from that list; use [] when none clearly applies - never invent a code.

        Respond with ONLY a single JSON object, no markdown fences, no commentary, matching
        exactly this shape:
        {
          "functionalRequirements": [
            {"title": "string", "actor": "string", "priority": "Low|Medium|High|Critical",
             "preconditions": "string", "inputs": "string", "processing": "string",
             "expectedResult": "string", "dependencies": ["string"], "businessObjectiveCodes": ["BO-001"], "sourceReferences": ["notes.pdf#0"]}
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
    private readonly IKnowledgeRetriever _retriever;
    private readonly ILogger<RequirementGenerationService> _logger;

    private const int DocumentExcerpts = 4;

    public RequirementGenerationService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IKnowledgeRetriever retriever, ILogger<RequirementGenerationService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _retriever = retriever;
        _logger = logger;
    }

    public async Task<GenerateRequirementsResult> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);

        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);

        // §21 — objectives come from the project record, so FRs can be traced back to them.
        var project = await _db.Projects.FirstAsync(p => p.Id == source.ProjectId, cancellationToken);
        var objectives = await BusinessObjectives.SyncAsync(_db, project.Id, project.Objectives, _currentUser.UserId, cancellationToken);
        var objectiveContext = GenerationSupport.BuildContextBlock("Business objectives of this project:", objectives);

        // §26 — ground generation in the project's uploaded documents (meeting notes, specs, ...).
        var retrieval = await _retriever.RetrieveAsync(project.Id, source.RawText, DocumentExcerpts, cancellationToken);
        var documentContext = retrieval.Chunks.Count == 0
            ? string.Empty
            : $"\n\nProject document excerpts (label in brackets):\n{KnowledgeRetriever.FormatExcerpts(retrieval.Chunks)}";

        var userPrompt = source.RawText + context + objectiveContext + documentContext;

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<GenerationAiResponse>(
            _chatClient, _logger, "RequirementGeneration", source.Id.ToString(), SystemPrompt, userPrompt, cancellationToken,
            knownArtifactCodes: objectives.Select(o => o.Code),
            knownSourceReferences: retrieval.Chunks.Select(c => c.Reference));

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
                Dependencies = item.Dependencies,
                SourceReferences = item.SourceReferences
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

        // FR -DerivedFrom-> BO: the requirement exists to serve that objective.
        for (var i = 0; i < frArtifacts.Count; i++)
        {
            var served = objectives.Where(o => parsed.FunctionalRequirements[i].BusinessObjectiveCodes
                .Contains(o.Code, StringComparer.OrdinalIgnoreCase));
            GenerationSupport.LinkToRelated(_db, frArtifacts[i], served, RelationshipType.DerivedFrom, now);
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

    private class GenerationAiResponse : IValidatableAiResponse
    {
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
            }, minItems: 1);
            v.Items(NonFunctionalRequirements, "nonFunctionalRequirements", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.OneOf(item.Category, $"{path}.category", AiVocabulary.NfrCategories);
            });
        }
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
    }

    private class NonFunctionalRequirementItem
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? TargetValue { get; set; }
    }
}
