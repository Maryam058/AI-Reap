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

    // NFR test cases: the quality is verified by how it is measured, under which conditions, and against
    // which pass criterion - and a target nobody has confirmed is never tested as if it were agreed.
    private const string NfrPromptTemplateVersion = "NfrTestCaseGeneration-v1";

    private const string NfrSystemPrompt = """
        You are a QA engineer (§20 of an SDLC automation spec). Given a non-functional requirement - a
        quality the system must have (performance, security, availability, scalability, usability,
        maintainability, compliance or observability) - write test cases that verify that quality. Each
        test case states how the quality is measured or checked, the conditions it is checked under
        (load, data volume, user roles, environment, failure being simulated) and the pass/fail
        criterion. Cover what applies to the category - for example load and response-time checks for
        performance, access-control and input-handling checks for security, failover and recovery for
        availability - and do not force a kind that doesn't apply.

        Never invent a numerical limit (an amount, duration, size, rate or count). Use the target value
        as the pass criterion only when "Target status" says it is confirmed. When the target is a
        proposed assumption, or there is none, write the expected result against "the agreed target"
        and say in the test case that the value is to be confirmed. A number listed under "Numbers not
        found in verified quotes" is unconfirmed. "Evidence status" says only whether the requirement's
        quotes were found in the source text, not whether the requirement is correct.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "testCases": [
            {"title": "string", "preconditions": "string or null", "steps": ["string"],
             "expectedResult": "string",
             "testKind": "performance|load|security|availability|reliability|scalability|usability|accessibility|maintainability|compliance|observability|positive|negative|boundary|permission|validation"}
          ]
        }
        """;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<ArtifactResponse>?> GenerateAsync(Guid requirementArtifactId, CancellationToken cancellationToken = default)
    {
        var fr = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == requirementArtifactId, cancellationToken);
        if (fr is null || fr.ArtifactType is not (ArtifactType.FunctionalRequirement or ArtifactType.NonFunctionalRequirement))
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(fr.ProjectId, cancellationToken);
        GenerationInputs.EnsureTestCaseTarget(fr);

        var isNfr = fr.ArtifactType == ArtifactType.NonFunctionalRequirement;
        var userPrompt = isNfr ? NfrPrompt(fr) : FrPrompt(fr);

        string rawResponse;
        TestCaseAiResponse parsed;
        if (isNfr)
        {
            (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<NfrTestCaseAiResponse>(
                _chatClient, _logger, "TestCaseGeneration", fr.Id.ToString(), NfrSystemPrompt, userPrompt, cancellationToken);
        }
        else
        {
            (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<TestCaseAiResponse>(
                _chatClient, _logger, "TestCaseGeneration", fr.Id.ToString(), SystemPrompt, userPrompt, cancellationToken);
        }

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

    // The FR prompt, exactly as before NFR support.
    private static string FrPrompt(Artifact fr)
    {
        var frPayload = FunctionalRequirementContext.Read(fr);
        return
            $"{fr.Code}: {fr.Title}\nActor: {frPayload.Actor}\nPreconditions: {frPayload.Preconditions}\n" +
            $"Processing: {frPayload.Processing}\nExpected result: {frPayload.ExpectedResult}" +
            FunctionalRequirementContext.EvidenceLines(frPayload);
    }

    // The NFR's category, description and target - with whether a person has confirmed that target
    // (NfrAssumption) - and its evidence results, labelled as the FR prompt labels them.
    public static string NfrPrompt(Artifact nfr)
    {
        var p = JsonSerializer.Deserialize<NonFunctionalRequirementPayload>(nfr.DataJson, ReadOptions) ?? new NonFunctionalRequirementPayload();
        var target = string.IsNullOrWhiteSpace(p.TargetValue) ? "none stated" : p.TargetValue.Trim();
        var targetStatus = string.IsNullOrWhiteSpace(p.TargetValue)
            ? "no target value - test against the agreed target, to be confirmed"
            : p.AssumptionStatus == "confirmed"
                ? "confirmed by a person - use it as the pass criterion"
                : "proposed assumption, not confirmed - do not test it as an agreed limit";
        var evidence = p.EvidenceCheck is null
            ? FunctionalRequirementContext.NotChecked
            : p.EvidenceStatus?.Trim().ToLowerInvariant() switch
            {
                RequirementEvidence.Verified => "verified (its quotes were found in the cited text; this does not confirm the requirement itself)",
                RequirementEvidence.Unverified => "unverified (at least one of its quotes was not found in the cited text)",
                RequirementEvidence.None => FunctionalRequirementContext.NoQuotes,
                null or "" => FunctionalRequirementContext.NotRecorded,
                var other => other
            };
        var numbers = p.EvidenceCheck is null
            ? FunctionalRequirementContext.NotChecked
            : p.UnsupportedNumbers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList() is { Count: > 0 } list
                ? string.Join(", ", list)
                : "none";
        var origin = string.IsNullOrWhiteSpace(p.RequirementOrigin) ? FunctionalRequirementContext.NotRecorded : p.RequirementOrigin.Trim();

        return
            $"{nfr.Code}: {nfr.Title}\nCategory: {(string.IsNullOrWhiteSpace(p.Category) ? "not specified" : p.Category)}\n" +
            $"Description: {(string.IsNullOrWhiteSpace(p.Description) ? "not specified" : p.Description)}\n" +
            $"Target value: {target}\nTarget status: {targetStatus}\n" +
            $"Requirement origin: {origin}\nEvidence status: {evidence}\nNumbers not found in verified quotes: {numbers}";
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
            _db, fr.ProjectId, "TestCaseGeneration",
            fr.ArtifactType == ArtifactType.NonFunctionalRequirement ? NfrPromptTemplateVersion : PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, fr.Id.ToString(), rawResponse, now,
            testArtifacts.Select(a => a.Id).ToList());

        return (testArtifacts, skipped);
    }

    private class TestCaseAiResponse : IValidatableAiResponse
    {
        public List<TestCaseItem> TestCases { get; set; } = new();

        protected virtual string[] Kinds => AiVocabulary.TestKinds;

        public void Validate(AiResponseValidator v) =>
            v.Items(TestCases, "testCases", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.Required(item.ExpectedResult, $"{path}.expectedResult");
                if (item.Steps is null || item.Steps.Count == 0) v.Fail($"{path}.steps must contain at least one step.");
                v.NoBlankEntries(item.Steps, $"{path}.steps");
                v.OneOf(item.TestKind, $"{path}.testKind", Kinds);
            }, minItems: 1);
    }

    private sealed class NfrTestCaseAiResponse : TestCaseAiResponse
    {
        protected override string[] Kinds { get; } = [.. AiVocabulary.NfrTestKinds, .. AiVocabulary.TestKinds];
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
