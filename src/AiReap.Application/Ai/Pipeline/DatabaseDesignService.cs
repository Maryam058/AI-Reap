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
    private const string PromptTemplateVersion = "DatabaseDesignGeneration-v3";
    // DatabaseDesignGeneration-v3 (A4b Phase 4) = DatabaseDesignGeneration-v2 plus each listed FR's source IDs (BuildFunctionalRequirementBlock)
    // and the rule that raw-text IDs are document IDs, not citation codes; an FR without source IDs keeps its line.

    private const string SystemPrompt = """
        You are a database designer (§17 of an SDLC automation spec). Given a raw requirement,
        its clarifications, and the functional requirements derived from it, propose the data
        entities needed: fields (with data type and whether required), keys, relationships to
        other entities, and indexes. Reference the functional requirement(s) each entity
        supports by code (e.g. "FR-001") in relatedRequirementCodes - only use codes given to
        you, never invent one.
        When functional requirements are listed, every entity must cite at least one of
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
        await GenerationInputs.EnsureNotAllRejectedAsync(_db, requirementSourceId, "design data entities for", cancellationToken);
        var functionalRequirements = await GenerationSupport.LoadGenerationInputsAsync(_db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildFunctionalRequirementBlock(functionalRequirements);

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<DatabaseDesignAiResponse>(
            _chatClient, _logger, "DatabaseDesignGeneration", source.Id.ToString(), SystemPrompt, source.RawText + context + frContext, cancellationToken,
            knownArtifactCodes: functionalRequirements.Select(fr => fr.Code));

        // G2 — one save (ArtifactPersistence) of the data entities (same entity name) not already saved for this source
        // (ArtifactFingerprints): a re-run with the same output adds nothing, and existing artifacts are
        // never changed. A unique-index race re-prepares against the database.
        var entityArtifacts = new List<Artifact>();
        var skipped = 0;
        await ArtifactPersistence.SaveAsync(_db, async ct =>
            (entityArtifacts, skipped) = await PrepareEntitiesAsync(source, parsed, functionalRequirements, rawResponse, ct), cancellationToken);

        if (skipped > 0)
        {
            _logger.LogInformation(
                "{Operation} for {Input}: {Created} created, {Skipped} already existed and were not saved again.",
                "DatabaseDesignGeneration", source.Id, entityArtifacts.Count, skipped);
        }

        return new GeneratedArtifacts(entityArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(), skipped);
    }

    // The data entities of the response not already saved for the source, added to the context with
    // their versions, fingerprints, links and audit rows - not saved here.
    private async Task<(List<Artifact> Created, int Skipped)> PrepareEntitiesAsync(
        RequirementSource source, DatabaseDesignAiResponse parsed, IReadOnlyList<Artifact> functionalRequirements, string rawResponse,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var existing = await ArtifactFingerprints.ExistingAsync(_db, source.Id, [ArtifactType.DataEntity], cancellationToken);
        var fresh = ArtifactFingerprints.Fresh(parsed.DataEntities, ArtifactType.DataEntity,
            e => ArtifactFingerprints.ForDataEntity(e.Title), existing, out var skipped);
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.DataEntity, fresh.Count, cancellationToken);

        var entityArtifacts = new List<Artifact>();

        for (var i = 0; i < fresh.Count; i++)
        {
            var item = fresh[i].Item;
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
            _db.ArtifactFingerprints.Add(ArtifactFingerprints.Record(artifact, fresh[i].Fingerprint, now));

            var related = GenerationSupport.ResolveCited(item.RelatedRequirementCodes, functionalRequirements);
            GenerationSupport.LinkToRelated(_db, artifact, related, RelationshipType.DerivedFrom, now);

            entityArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "DatabaseDesignGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            entityArtifacts.Select(a => a.Id).ToList());

        return (entityArtifacts, skipped);
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
                v.CodesRequired(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
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
