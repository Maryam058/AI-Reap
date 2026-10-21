using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public class ApiDesignService : IApiDesignService
{
    private const string PromptTemplateVersion = "ApiDesignGeneration-v3";
    // ApiDesignGeneration-v3 (A4b Phase 4) = ApiDesignGeneration-v2 plus each listed FR's source IDs (BuildFunctionalRequirementBlock)
    // and the rule that raw-text IDs are document IDs, not citation codes; an FR without source IDs keeps its line.

    private const string SystemPrompt = """
        You are an API designer (§18 of an SDLC automation spec). Given a raw requirement, its
        clarifications, and the functional requirements derived from it, propose the API
        endpoints needed: purpose, HTTP method, route, request shape, response shape,
        validation rules, and an authorization policy. Reference the functional requirement(s)
        each endpoint realizes by code (e.g. "FR-001") in relatedRequirementCodes - only use
        codes given to you, never invent one.
        When functional requirements are listed, every endpoint must cite at least one of
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
          "apiSpecifications": [
            {"title": "string", "purpose": "string", "method": "GET|POST|PUT|PATCH|DELETE",
             "route": "string", "request": "string or null", "response": "string or null",
             "validation": "string or null", "authorization": "string or null",
             "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<ApiDesignService> _logger;

    public ApiDesignService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<ApiDesignService> logger)
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
        await GenerationInputs.EnsureNotAllRejectedAsync(_db, requirementSourceId, "design API endpoints for", cancellationToken);
        var functionalRequirements = await GenerationSupport.LoadGenerationInputsAsync(_db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildFunctionalRequirementBlock(functionalRequirements);

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<ApiDesignAiResponse>(
            _chatClient, _logger, "ApiDesignGeneration", source.Id.ToString(), SystemPrompt, source.RawText + context + frContext, cancellationToken,
            knownArtifactCodes: functionalRequirements.Select(fr => fr.Code));

        // G2 — one save (ArtifactPersistence) of the API endpoints (same method and route) not already saved for this source
        // (ArtifactFingerprints): a re-run with the same output adds nothing, and existing artifacts are
        // never changed. A unique-index race re-prepares against the database.
        var apiArtifacts = new List<Artifact>();
        var skipped = 0;
        await ArtifactPersistence.SaveAsync(_db, async ct =>
            (apiArtifacts, skipped) = await PrepareEndpointsAsync(source, parsed, functionalRequirements, rawResponse, ct), cancellationToken);

        if (skipped > 0)
        {
            _logger.LogInformation(
                "{Operation} for {Input}: {Created} created, {Skipped} already existed and were not saved again.",
                "ApiDesignGeneration", source.Id, apiArtifacts.Count, skipped);
        }

        return new GeneratedArtifacts(apiArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(), skipped);
    }

    // The API endpoints of the response not already saved for the source, added to the context with
    // their versions, fingerprints, links and audit rows - not saved here.
    private async Task<(List<Artifact> Created, int Skipped)> PrepareEndpointsAsync(
        RequirementSource source, ApiDesignAiResponse parsed, IReadOnlyList<Artifact> functionalRequirements, string rawResponse,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var existing = await ArtifactFingerprints.ExistingAsync(_db, source.Id, [ArtifactType.ApiSpecification], cancellationToken);
        var fresh = ArtifactFingerprints.Fresh(parsed.ApiSpecifications, ArtifactType.ApiSpecification,
            a => ArtifactFingerprints.ForApiSpecification(a.Method, a.Route), existing, out var skipped);
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.ApiSpecification, fresh.Count, cancellationToken);

        var apiArtifacts = new List<Artifact>();

        for (var i = 0; i < fresh.Count; i++)
        {
            var item = fresh[i].Item;
            var payload = new ApiSpecificationPayload
            {
                Purpose = item.Purpose,
                Method = item.Method,
                Route = item.Route,
                Request = item.Request,
                Response = item.Response,
                Validation = item.Validation,
                Authorization = item.Authorization
            };

            var artifact = GenerationSupport.NewArtifact(
                source, ArtifactType.ApiSpecification, codes[i], item.Title, null, JsonSerializer.Serialize(payload), _currentUser.UserId, now);

            _db.Artifacts.Add(artifact);
            _db.ArtifactVersions.Add(GenerationSupport.InitialVersion(artifact, "AI API design", _currentUser.UserId, now));
            _db.ArtifactFingerprints.Add(ArtifactFingerprints.Record(artifact, fresh[i].Fingerprint, now));

            var related = GenerationSupport.ResolveCited(item.RelatedRequirementCodes, functionalRequirements);
            GenerationSupport.LinkToRelated(_db, artifact, related, RelationshipType.DerivedFrom, now);

            apiArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "ApiDesignGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            apiArtifacts.Select(a => a.Id).ToList());

        return (apiArtifacts, skipped);
    }

    private class ApiDesignAiResponse : IValidatableAiResponse
    {
        public List<ApiSpecificationItem> ApiSpecifications { get; set; } = new();

        public void Validate(AiResponseValidator v) =>
            v.Items(ApiSpecifications, "apiSpecifications", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.OneOf(item.Method, $"{path}.method", AiVocabulary.HttpMethods);
                v.Required(item.Route, $"{path}.route");
                if (!string.IsNullOrWhiteSpace(item.Route) && !item.Route.TrimStart().StartsWith('/'))
                {
                    v.Fail($"{path}.route must start with '/'.");
                }
                v.CodesExist(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
                v.CodesRequired(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
            }, minItems: 1);
    }

    private class ApiSpecificationItem
    {
        public string Title { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Route { get; set; } = string.Empty;
        public string? Request { get; set; }
        public string? Response { get; set; }
        public string? Validation { get; set; }
        public string? Authorization { get; set; }
        public List<string> RelatedRequirementCodes { get; set; } = new();
    }
}
