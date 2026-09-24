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

public class TestGenerationService : ITestGenerationService
{
    private const string PromptTemplateVersion = "TestCaseGeneration-v1";

    private const string SystemPrompt = """
        You are a QA engineer (§20 of an SDLC automation spec). Given a functional requirement,
        write test cases covering positive, negative, boundary, permission/security, and
        validation scenarios as appropriate - do not force a category that doesn't apply.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "testCases": [
            {"title": "string", "preconditions": "string or null", "steps": ["string"],
             "expectedResult": "string", "testKind": "positive|negative|boundary|permission|validation"}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<TestGenerationService> _logger;

    public TestGenerationService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<TestGenerationService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ArtifactResponse>?> GenerateAsync(Guid functionalRequirementArtifactId, CancellationToken cancellationToken = default)
    {
        var fr = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == functionalRequirementArtifactId, cancellationToken);
        if (fr is null || fr.ArtifactType != ArtifactType.FunctionalRequirement)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(fr.ProjectId, cancellationToken);

        var frPayload = JsonSerializer.Deserialize<FunctionalRequirementPayload>(
            fr.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FunctionalRequirementPayload();

        var userPrompt =
            $"{fr.Code}: {fr.Title}\nActor: {frPayload.Actor}\nPreconditions: {frPayload.Preconditions}\n" +
            $"Processing: {frPayload.Processing}\nExpected result: {frPayload.ExpectedResult}";

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<TestCaseAiResponse>(
            _chatClient, _logger, "TestCaseGeneration", fr.Id.ToString(), SystemPrompt, userPrompt, cancellationToken);

        var now = DateTime.UtcNow;
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, fr.ProjectId, ArtifactType.TestCase, parsed.TestCases.Count, cancellationToken);

        var testArtifacts = new List<Artifact>();

        for (var i = 0; i < parsed.TestCases.Count; i++)
        {
            var item = parsed.TestCases[i];
            var payload = new TestCasePayload
            {
                Preconditions = item.Preconditions,
                Steps = item.Steps,
                ExpectedResult = item.ExpectedResult,
                TestKind = item.TestKind
            };

            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = fr.ProjectId,
                ArtifactType = ArtifactType.TestCase,
                Code = codes[i],
                Title = item.Title,
                Status = ArtifactStatus.AiGenerated,
                Origin = ArtifactOrigin.Ai,
                RequirementSourceId = fr.RequirementSourceId,
                DataJson = JsonSerializer.Serialize(payload),
                CurrentVersion = 1,
                CreatedByUserId = _currentUser.UserId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Artifacts.Add(artifact);
            _db.ArtifactVersions.Add(GenerationSupport.InitialVersion(artifact, "AI test case generation", _currentUser.UserId, now));

            // FR "is tested by" the new TestCase — source is the FR, per ADR-002's TestedBy semantics.
            _db.ArtifactRelationships.Add(new ArtifactRelationship
            {
                Id = Guid.NewGuid(),
                SourceArtifactId = fr.Id,
                TargetArtifactId = artifact.Id,
                RelationshipType = RelationshipType.TestedBy,
                CreatedAt = now
            });

            testArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, fr.ProjectId, "TestCaseGeneration", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, fr.Id.ToString(), rawResponse, now,
            testArtifacts.Select(a => a.Id).ToList());

        await _db.SaveChangesAsync(cancellationToken);

        return testArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    private class TestCaseAiResponse : IValidatableAiResponse
    {
        public List<TestCaseItem> TestCases { get; set; } = new();

        public void Validate(AiResponseValidator v) =>
            v.Items(TestCases, "testCases", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.Required(item.ExpectedResult, $"{path}.expectedResult");
                if (item.Steps is null || item.Steps.Count == 0) v.Fail($"{path}.steps must contain at least one step.");
                v.NoBlankEntries(item.Steps, $"{path}.steps");
                v.OneOf(item.TestKind, $"{path}.testKind", AiVocabulary.TestKinds);
            }, minItems: 1);
    }

    private class TestCaseItem
    {
        public string Title { get; set; } = string.Empty;
        public string? Preconditions { get; set; }
        public List<string> Steps { get; set; } = new();
        public string ExpectedResult { get; set; } = string.Empty;
        public string TestKind { get; set; } = "positive";
    }
}
