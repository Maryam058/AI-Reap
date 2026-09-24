using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public class DatabaseDesignService : IDatabaseDesignService
{
    private const string PromptTemplateVersion = "DatabaseDesignGeneration-v1";

    private const string SystemPrompt = """
        You are a database designer (§17 of an SDLC automation spec). Given a raw requirement,
        its clarifications, and the functional requirements derived from it, propose the data
        entities needed: fields (with data type and whether required), keys, relationships to
        other entities, and indexes. Reference the functional requirement(s) each entity
        supports by code (e.g. "FR-001") in relatedRequirementCodes - only use codes given to
        you, never invent one.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "dataEntities": [
            {"title": "string",
             "fields": [{"name": "string", "dataType": "string", "required": true}],
             "keys": ["string"], "relationships": "string or null", "indexes": ["string"],
             "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<DatabaseDesignService> _logger;

    public DatabaseDesignService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<DatabaseDesignService> logger)
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
        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildContextBlock("Functional requirements derived so far:", functionalRequirements);

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<DatabaseDesignAiResponse>(
            _chatClient, _logger, "DatabaseDesignGeneration", source.Id.ToString(), SystemPrompt, source.RawText + context + frContext, cancellationToken,
            knownArtifactCodes: functionalRequirements.Select(fr => fr.Code));

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.DataEntity, parsed.DataEntities.Count, cancellationToken);

        var entityArtifacts = new List<Artifact>();

        for (var i = 0; i < parsed.DataEntities.Count; i++)
        {
            var item = parsed.DataEntities[i];
            var payload = new DataEntityPayload
            {
                Fields = item.Fields.Select(f => new DataEntityField { Name = f.Name, DataType = f.DataType, Required = f.Required }).ToList(),
                Keys = item.Keys,
                Relationships = item.Relationships,
                Indexes = item.Indexes
            };

            var artifact = GenerationSupport.NewArtifact(
                source, ArtifactType.DataEntity, codes[i], item.Title, null, JsonSerializer.Serialize(payload), _currentUser.UserId, now);

            _db.Artifacts.Add(artifact);
            _db.ArtifactVersions.Add(GenerationSupport.InitialVersion(artifact, "AI database design", _currentUser.UserId, now));

            var related = functionalRequirements.Where(fr => item.RelatedRequirementCodes.Contains(fr.Code));
            GenerationSupport.LinkToRelated(_db, artifact, related, RelationshipType.DerivedFrom, now);

            entityArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "DatabaseDesignGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            entityArtifacts.Select(a => a.Id).ToList());

        await _db.SaveChangesAsync(cancellationToken);

        return entityArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    private class DatabaseDesignAiResponse : IValidatableAiResponse
    {
        public List<DataEntityItem> DataEntities { get; set; } = new();

        public void Validate(AiResponseValidator v) =>
            v.Items(DataEntities, "dataEntities", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.Items(item.Fields, $"{path}.fields", (field, fieldPath) =>
                {
                    v.Required(field.Name, $"{fieldPath}.name");
                    v.Required(field.DataType, $"{fieldPath}.dataType");
                }, minItems: 1);
                v.CodesExist(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
            }, minItems: 1);
    }

    private class DataEntityItem
    {
        public string Title { get; set; } = string.Empty;
        public List<DataEntityFieldItem> Fields { get; set; } = new();
        public List<string> Keys { get; set; } = new();
        public string? Relationships { get; set; }
        public List<string> Indexes { get; set; } = new();
        public List<string> RelatedRequirementCodes { get; set; } = new();
    }

    private class DataEntityFieldItem
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public bool Required { get; set; }
    }
}
