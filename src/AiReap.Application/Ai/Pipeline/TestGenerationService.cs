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
    // v2 (A4b Phase 3) = v1's prompt unchanged as a prefix, plus the FR's inputs, origin and evidence
    // results (FunctionalRequirementContext) and the rule against invented or unconfirmed limits.
    private const string PromptTemplateVersion = "TestCaseGeneration-v2";

    private const string SystemPrompt = """
        You are a QA engineer (§20 of an SDLC automation spec). Given a functional requirement,
        write test cases covering positive, negative, boundary, permission/security, and
        validation scenarios as appropriate - do not force a category that doesn't apply.

        Never invent a numerical limit (an amount, duration, size or count): use one only if it is
        written in the requirement. A number listed under "Numbers not found in verified quotes" is
        unconfirmed - do not test it as a confirmed rule; say in the test case that the value is to
        be confirmed. "Evidence status" says only whether the requirement's quotes were found in the
        source text, not whether the requirement is correct.

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
        GenerationInputs.EnsureTestCaseTarget(fr);

        var frPayload = FunctionalRequirementContext.Read(fr);

        var userPrompt =
            $"{fr.Code}: {fr.Title}\nActor: {frPayload.Actor}\nPreconditions: {frPayload.Preconditions}\n" +
            $"Processing: {frPayload.Processing}\nExpected result: {frPayload.ExpectedResult}" +
            FunctionalRequirementContext.EvidenceLines(frPayload);

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<TestCaseAiResponse>(
            _chatClient, _logger, "TestCaseGeneration", fr.Id.ToString(), SystemPrompt, userPrompt, cancellationToken);

        // G2 — one save of the test cases not already saved for this requirement (ArtifactFingerprints,
        // scoped to the FR: the same wording under another FR is a different test case). Existing test
        // cases are never changed.
        var testArtifacts = new List<Artifact>();
        var skipped = 0;
        await ArtifactPersistence.SaveAsync(_db, async ct =>
            (testArtifacts, skipped) = await PrepareTestCasesAsync(fr, parsed, rawResponse, ct), cancellationToken);

        if (skipped > 0)
        {
            _logger.LogInformation(
                "{Operation} for {Input}: {Created} created, {Skipped} already existed and were not saved again.",
                "TestCaseGeneration", fr.Id, testArtifacts.Count, skipped);
        }

        return new GeneratedArtifacts(testArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(), skipped);
    }

    // The test cases of the response not already saved for the FR, added to the context with their
    // versions, fingerprints, links and audit rows - not saved here.
    private async Task<(List<Artifact> Created, int Skipped)> PrepareTestCasesAsync(
        Artifact fr, TestCaseAiResponse parsed, string rawResponse, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var children = await _db.ArtifactRelationships
            .Where(r => r.SourceArtifactId == fr.Id && r.RelationshipType == RelationshipType.TestedBy)
            .Select(r => r.TargetArtifactId)
            .ToListAsync(cancellationToken);
        var existing = await ArtifactFingerprints.ExistingForParentAsync(
            _db, fr.RequirementSourceId, ArtifactType.TestCase, fr.Id, children, cancellationToken);
        var fresh = ArtifactFingerprints.Fresh(parsed.TestCases, ArtifactType.TestCase,
            t => ArtifactFingerprints.ForTestCase(fr.Id, t.Preconditions, t.Steps, t.ExpectedResult), existing, out var skipped);
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(_db, fr.ProjectId, ArtifactType.TestCase, fresh.Count, cancellationToken);

        var testArtifacts = new List<Artifact>();

        for (var i = 0; i < fresh.Count; i++)
        {
            var item = fresh[i].Item;
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
            if (ArtifactFingerprints.TryRecord(artifact, fresh[i].Fingerprint, now) is { } fingerprint)
            {
                _db.ArtifactFingerprints.Add(fingerprint);
            }

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

        return (testArtifacts, skipped);
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
