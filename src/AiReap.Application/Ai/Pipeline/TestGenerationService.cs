using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

public class TestGenerationService : ITestGenerationService
{
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

    public TestGenerationService(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ArtifactResponse>?> GenerateAsync(Guid functionalRequirementArtifactId, CancellationToken cancellationToken = default)
    {
        var fr = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == functionalRequirementArtifactId, cancellationToken);
        if (fr is null || fr.ArtifactType != ArtifactType.FunctionalRequirement)
        {
            return null;
        }

        var frPayload = JsonSerializer.Deserialize<FunctionalRequirementPayload>(
            fr.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FunctionalRequirementPayload();

        var userPrompt =
            $"{fr.Code}: {fr.Title}\nActor: {frPayload.Actor}\nPreconditions: {frPayload.Preconditions}\n" +
            $"Processing: {frPayload.Processing}\nExpected result: {frPayload.ExpectedResult}";

        var rawResponse = await _chatClient.CompleteAsync(SystemPrompt, userPrompt, cancellationToken);
        var parsed = AiJsonParser.Parse<TestCaseAiResponse>(rawResponse);

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

        _db.AIExecutions.Add(GenerationSupport.NewExecution(fr.ProjectId, "TestCaseGeneration", _currentUser.UserId, _chatClient.ModelName, fr.Id.ToString(), rawResponse, now));

        await _db.SaveChangesAsync(cancellationToken);

        return testArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList();
    }

    private class TestCaseAiResponse
    {
        public List<TestCaseItem> TestCases { get; set; } = new();
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
