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
    private const string PromptTemplateVersion = "ApiDesignGeneration-v1";

    private const string SystemPrompt = """
        You are an API designer (§18 of an SDLC automation spec). Given a raw requirement, its
        clarifications, and the functional requirements derived from it, propose the API
        endpoints needed: purpose, HTTP method, route, request shape, response shape,
        validation rules, and an authorization policy. Reference the functional requirement(s)
        each endpoint realizes by code (e.g. "FR-001") in relatedRequirementCodes - only use
        codes given to you, never invent one.

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
        var functionalRequirements = await GenerationSupport.LoadArtifactsAsync(_db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildContextBlock("Functional requirements derived so far:", functionalRequirements);

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<ApiDesignAiResponse>(
            _chatClient, _logger, "ApiDesignGeneration", source.Id.ToString(), SystemPrompt, source.RawText + context + frContext, cancellationToken);

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.ApiSpecification, parsed.ApiSpecifications.Count, cancellationToken);

        var apiArtifacts = new List<Artifact>();

        for (var i = 0; i < parsed.ApiSpecifications.Count; i++)
        {
            var item = parsed.ApiSpecifications[i];
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

            var related = functionalRequirements.Where(fr => item.RelatedRequirementCodes.Contains(fr.Code));
            GenerationSupport.LinkToRelated(_db, artifact, related, RelationshipType.DerivedFrom, now);

            apiArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "ApiDesignGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            apiArtifacts.Select(a => a.Id).ToList());

        await _db.SaveChangesAsync(cancellationToken);

        return apiArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    private class ApiDesignAiResponse
    {
        public List<ApiSpecificationItem> ApiSpecifications { get; set; } = new();
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
