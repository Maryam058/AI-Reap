using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
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

    public async Task<ArtifactResponse> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default) =>
        (await GenerateWithOutcomeAsync(requirementSourceId, cancellationToken)).Design;

    public async Task<(ArtifactResponse Design, bool Created)> GenerateWithOutcomeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await GenerationSupport.LoadSourceAsync(_db, requirementSourceId, cancellationToken);
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        await GenerationInputs.EnsureNotAllRejectedAsync(_db, requirementSourceId, "design against", cancellationToken);
        var functionalRequirements = await GenerationSupport.LoadGenerationInputsAsync(_db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildContextBlock(GenerationSupport.FunctionalRequirementsHeading, functionalRequirements);

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<DesignAiResponse>(
            _chatClient, _logger, "SolutionDesignGeneration", source.Id.ToString(), SystemPrompt, source.RawText + context + frContext, cancellationToken);

        // G2 — the same design (title and architecture overview) already saved for this source is not
        // saved again: the existing design is returned unchanged and nothing new is created.
        Artifact? created = null;
        ArtifactResponse? design = null;
        await ArtifactPersistence.SaveAsync(_db, async ct =>
            (created, design) = await PrepareDesignAsync(source, parsed, rawResponse, ct), cancellationToken);

        if (created is null)
        {
            _logger.LogInformation(
                "{Operation} for {Input}: 0 created, 1 already existed and was not saved again.", "SolutionDesignGeneration", source.Id);
            return (design!, false);
        }

        return (ArtifactResponseMapper.ToResponse(created), true);
    }

    // The design of the response, added to the context with its version, fingerprint and audit row - or,
    // when the same design is already saved for the source, only the audit row, and the existing design.
    private async Task<(Artifact? Created, ArtifactResponse? Existing)> PrepareDesignAsync(
        RequirementSource source, DesignAiResponse parsed, string rawResponse, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var fingerprint = ArtifactFingerprints.ForSolutionDesign(parsed.Title, parsed.ArchitectureOverview);
        var existing = await ArtifactFingerprints.ExistingAsync(_db, source.Id, [ArtifactType.DesignArtifact], cancellationToken);
        if (existing.Contains((ArtifactType.DesignArtifact, fingerprint)))
        {
            GenerationSupport.AddExecutions(
                _db, source.ProjectId, "SolutionDesignGeneration", PromptTemplateVersion, _currentUser.UserId,
                _chatClient.ModelName, source.Id.ToString(), rawResponse, now, Array.Empty<Guid>());
            return (null, await ExistingDesignAsync(source.Id, fingerprint, cancellationToken));
        }

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
        _db.ArtifactFingerprints.Add(ArtifactFingerprints.Record(artifact, fingerprint, now));
        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "SolutionDesignGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now, new[] { artifact.Id });

        return (artifact, null);
    }

    // The saved design with this fingerprint: by its current content, else by the fingerprint it was created with.
    private async Task<ArtifactResponse> ExistingDesignAsync(Guid sourceId, string fingerprint, CancellationToken cancellationToken)
    {
        var designs = await GenerationSupport.LoadArtifactsAsync(_db, sourceId, ArtifactType.DesignArtifact, cancellationToken);
        var recorded = await _db.ArtifactFingerprints.AsNoTracking()
            .Where(f => f.RequirementSourceId == sourceId && f.ArtifactType == ArtifactType.DesignArtifact && f.Fingerprint == fingerprint)
            .Select(f => f.ArtifactId)
            .ToListAsync(cancellationToken);
        var match = designs.FirstOrDefault(d => ArtifactFingerprints.ForCurrentContent(d) == fingerprint)
            ?? designs.First(d => recorded.Contains(d.Id));
        return ArtifactResponseMapper.ToResponse(match);
    }

    private class DesignAiResponse : IValidatableAiResponse
    {
        public void Validate(AiResponseValidator v)
        {
            v.Required(Title, "title");
            v.MaxLength(Title, 300, "title");
            v.Required(ArchitectureOverview, "architectureOverview");
            v.NoBlankEntries(Modules, "modules");
        }

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
