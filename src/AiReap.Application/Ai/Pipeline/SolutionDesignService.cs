using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public class SolutionDesignService : ISolutionDesignService
{
    private const string PromptTemplateVersion = "SolutionDesignGeneration-v1";

    private const string SystemPrompt = """
        You are a solution architect (§16 of an SDLC automation spec). Given a raw requirement,
        its clarifications, and the functional requirements derived from it, propose an
        architecture-level design. Do not propose specific API endpoints or database schemas -
        those are handled separately.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "title": "string",
          "architectureOverview": "string",
          "modules": ["string"],
          "integrationPoints": "string or null",
          "authentication": "string or null",
          "backgroundProcessing": "string or null",
          "caching": "string or null",
          "logging": "string or null",
          "deploymentConsiderations": "string or null"
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<SolutionDesignService> _logger;

    public SolutionDesignService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<SolutionDesignService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _logger = logger;
    }

    public async Task<ArtifactResponse> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await GenerationSupport.LoadSourceAsync(_db, requirementSourceId, cancellationToken);
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        var functionalRequirements = await GenerationSupport.LoadArtifactsAsync(_db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildContextBlock("Functional requirements derived so far:", functionalRequirements);

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<DesignAiResponse>(
            _chatClient, _logger, "SolutionDesignGeneration", source.Id.ToString(), SystemPrompt, source.RawText + context + frContext, cancellationToken);

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, source.ProjectId, ArtifactType.DesignArtifact, 1, cancellationToken);

        var payload = new DesignArtifactPayload
        {
            ArchitectureOverview = parsed.ArchitectureOverview,
            Modules = parsed.Modules,
            IntegrationPoints = parsed.IntegrationPoints,
            Authentication = parsed.Authentication,
            BackgroundProcessing = parsed.BackgroundProcessing,
            Caching = parsed.Caching,
            Logging = parsed.Logging,
            DeploymentConsiderations = parsed.DeploymentConsiderations
        };

        var artifact = GenerationSupport.NewArtifact(
            source, ArtifactType.DesignArtifact, codes[0], parsed.Title, null, JsonSerializer.Serialize(payload), _currentUser.UserId, now);

        _db.Artifacts.Add(artifact);
        _db.ArtifactVersions.Add(GenerationSupport.InitialVersion(artifact, "AI solution design", _currentUser.UserId, now));
        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "SolutionDesignGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now, new[] { artifact.Id });

        await _db.SaveChangesAsync(cancellationToken);

        return ArtifactResponseMapper.ToResponse(artifact);
    }

    private class DesignAiResponse
    {
        public string Title { get; set; } = "Solution Design";
        public string ArchitectureOverview { get; set; } = string.Empty;
        public List<string> Modules { get; set; } = new();
        public string? IntegrationPoints { get; set; }
        public string? Authentication { get; set; }
        public string? BackgroundProcessing { get; set; }
        public string? Caching { get; set; }
        public string? Logging { get; set; }
        public string? DeploymentConsiderations { get; set; }
    }
}
