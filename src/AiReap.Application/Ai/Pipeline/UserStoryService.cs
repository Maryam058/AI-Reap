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

public class UserStoryService : IUserStoryService
{
    private const string StoryPromptTemplateVersion = "UserStoryGeneration-v3";
    // UserStoryGeneration-v3 (A4b Phase 4) = UserStoryGeneration-v2 plus each listed FR's source IDs (BuildFunctionalRequirementBlock)
    // and the rule that raw-text IDs are document IDs, not citation codes; an FR without source IDs keeps its line.

    // v2 (A4b Phase 3) = v1 plus the story's eligible linked FRs (FunctionalRequirementContext) and the
    // rule against invented or unconfirmed numerical limits; a story without one keeps v1's user prompt.
    private const string AcceptanceCriteriaPromptTemplateVersion = "AcceptanceCriteriaGeneration-v2";

    private const string StorySystemPrompt = """
        You are a requirements engineer (§12 of an SDLC automation spec). Given a raw
        requirement, its clarifications, and the functional requirements already derived from
        it, write user stories. Reference the functional requirement(s) each story realizes by
        their code (e.g. "FR-001") in relatedRequirementCodes - only use codes that were given
        to you, never invent one.
        When functional requirements are listed, every story must cite at least one of
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
          "userStories": [
            {"title": "string", "persona": "string", "valueStatement": "string",
             "priority": "Low|Medium|High|Critical", "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private const string AcceptanceCriteriaSystemPrompt = """
        You are a QA-minded requirements engineer (§13 of an SDLC automation spec). Given a
        user story, write acceptance criteria in Given/When/Then form, covering positive,
        negative, and boundary cases.

        When linked functional requirements are listed, base the criteria on them. They are linked
        to the story for traceability; a link is not evidence from the requirement source.
        Never invent a numerical limit (an amount, duration, size or count): use one only if it is
        written in the story or a linked functional requirement. A number listed under "Numbers not
        found in verified quotes" is unconfirmed - do not state it as a confirmed rule; say the value
        is to be confirmed. For a boundary case with no stated limit, describe the boundary in words.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "acceptanceCriteria": [
            {"title": "string", "given": "string", "when": "string", "then": "string",
             "kind": "positive|negative|boundary"}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<UserStoryService> _logger;

    public UserStoryService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<UserStoryService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> GenerateStoriesAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        await GenerationInputs.EnsureNotAllRejectedAsync(_db, requirementSourceId, "generate user stories from", cancellationToken);

        var functionalRequirements = await GenerationSupport.LoadGenerationInputsAsync(
            _db, requirementSourceId, ArtifactType.FunctionalRequirement, cancellationToken);

        var context = await ClarificationContextBuilder.BuildAsync(_db, requirementSourceId, cancellationToken);
        var frContext = GenerationSupport.BuildFunctionalRequirementBlock(functionalRequirements);

        var userPrompt = source.RawText + context + frContext;

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<StoryAiResponse>(
            _chatClient, _logger, "UserStoryGeneration", source.Id.ToString(), StorySystemPrompt, userPrompt, cancellationToken,
            knownArtifactCodes: functionalRequirements.Select(fr => fr.Code));

        // G2 — one save (ArtifactPersistence) of the stories not already saved for this source
        // (ArtifactFingerprints): a re-run with the same output adds nothing, and existing stories are
        // never changed. A unique-index race re-prepares against the database.
        var storyArtifacts = new List<Artifact>();
        var skipped = 0;
        await ArtifactPersistence.SaveAsync(_db, async ct =>
            (storyArtifacts, skipped) = await PrepareStoriesAsync(source, parsed, functionalRequirements, rawResponse, ct), cancellationToken);

        LogSkipped("UserStoryGeneration", source.Id, storyArtifacts.Count, skipped);
        return new GeneratedArtifacts(storyArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(), skipped);
    }

    // The stories of the response not already saved for the source, added to the context with their
    // versions, fingerprints, links and audit rows - not saved here.
    private async Task<(List<Artifact> Created, int Skipped)> PrepareStoriesAsync(
        RequirementSource source, StoryAiResponse parsed, IReadOnlyList<Artifact> functionalRequirements, string rawResponse,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var existing = await ArtifactFingerprints.ExistingAsync(_db, source.Id, [ArtifactType.UserStory], cancellationToken);
        var fresh = ArtifactFingerprints.Fresh(parsed.UserStories, ArtifactType.UserStory,
            s => ArtifactFingerprints.ForUserStory(s.Persona, s.Title, s.ValueStatement), existing, out var skipped);
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, source.ProjectId, ArtifactType.UserStory, fresh.Count, cancellationToken);

        var storyArtifacts = new List<Artifact>();
        var relationships = new List<ArtifactRelationship>();

        for (var i = 0; i < fresh.Count; i++)
        {
            var item = fresh[i].Item;
            var payload = new UserStoryPayload { Persona = item.Persona, ValueStatement = item.ValueStatement };

            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = source.ProjectId,
                ArtifactType = ArtifactType.UserStory,
                Code = codes[i],
                Title = item.Title,
                Priority = Enum.TryParse<ArtifactPriority>(item.Priority, true, out var priority) ? priority : ArtifactPriority.Medium,
                Status = ArtifactStatus.AiGenerated,
                Origin = ArtifactOrigin.Ai,
                RequirementSourceId = source.Id,
                DataJson = JsonSerializer.Serialize(payload),
                CurrentVersion = 1,
                CreatedByUserId = _currentUser.UserId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Artifacts.Add(artifact);
            _db.ArtifactVersions.Add(new ArtifactVersion
            {
                Id = Guid.NewGuid(),
                ArtifactId = artifact.Id,
                VersionNumber = 1,
                DataSnapshotJson = artifact.DataJson,
                Title = artifact.Title,
                Priority = artifact.Priority,
                Status = artifact.Status,
                ChangedByUserId = _currentUser.UserId,
                ChangedAt = now,
                Reason = "AI user story generation",
                Origin = ArtifactOrigin.Ai
            });
            _db.ArtifactFingerprints.Add(ArtifactFingerprints.Record(artifact, fresh[i].Fingerprint, now));

            foreach (var relatedFr in GenerationSupport.ResolveCited(item.RelatedRequirementCodes, functionalRequirements))
            {
                relationships.Add(new ArtifactRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceArtifactId = artifact.Id,
                    TargetArtifactId = relatedFr.Id,
                    RelationshipType = RelationshipType.DerivedFrom,
                    CreatedAt = now
                });
            }

            storyArtifacts.Add(artifact);
        }

        foreach (var relationship in relationships)
        {
            _db.ArtifactRelationships.Add(relationship);
        }

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "UserStoryGeneration", StoryPromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, now,
            storyArtifacts.Select(a => a.Id).ToList());

        return (storyArtifacts, skipped);
    }

    private void LogSkipped(string operation, Guid input, int created, int skipped)
    {
        if (skipped > 0)
        {
            _logger.LogInformation(
                "{Operation} for {Input}: {Created} created, {Skipped} already existed and were not saved again.",
                operation, input, created, skipped);
        }
    }

    public async Task<IReadOnlyList<ArtifactResponse>?> GenerateAcceptanceCriteriaAsync(Guid userStoryArtifactId, CancellationToken cancellationToken = default)
    {
        var story = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == userStoryArtifactId, cancellationToken);
        if (story is null || story.ArtifactType != ArtifactType.UserStory)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(story.ProjectId, cancellationToken);
        var linkedFrs = (await GenerationInputs.LinkedFunctionalRequirementsAsync(_db, [story.Id], cancellationToken))[story.Id].ToList();
        GenerationInputs.EnsureAcceptanceCriteriaTarget(story, linkedFrs);

        var payload = JsonSerializer.Deserialize<UserStoryPayload>(
            story.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new UserStoryPayload();

        // A4b Phase 3 — the eligible linked FRs follow the story, never a rejected one; with none, the
        // prompt is exactly v1's. Sent whole or refused before sending (CallAiAndParseLosslessAsync).
        var userPrompt =
            $"Title: {story.Title}\nPersona: {payload.Persona}\nValue: {payload.ValueStatement}" +
            FunctionalRequirementContext.LinkedBlock(linkedFrs.Where(GenerationInputs.IsEligible));

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<AcceptanceCriteriaAiResponse>(
            _chatClient, _logger, "AcceptanceCriteriaGeneration", story.Id.ToString(), AcceptanceCriteriaSystemPrompt, userPrompt, cancellationToken);

        // G2 — one save of the criteria not already saved for this story (ArtifactFingerprints, scoped
        // to the story: the same wording under another story is a different criterion). Existing
        // criteria are never changed.
        var acArtifacts = new List<Artifact>();
        var skipped = 0;
        await ArtifactPersistence.SaveAsync(_db, async ct =>
            (acArtifacts, skipped) = await PrepareCriteriaAsync(story, parsed, rawResponse, ct), cancellationToken);

        LogSkipped("AcceptanceCriteriaGeneration", story.Id, acArtifacts.Count, skipped);
        return new GeneratedArtifacts(acArtifacts.Select(ArtifactResponseMapper.ToResponse).ToList(), skipped);
    }

    // The criteria of the response not already saved for the story, added to the context with their
    // versions, fingerprints, links and audit rows - not saved here.
    private async Task<(List<Artifact> Created, int Skipped)> PrepareCriteriaAsync(
        Artifact story, AcceptanceCriteriaAiResponse parsed, string rawResponse, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var children = await _db.ArtifactRelationships
            .Where(r => r.TargetArtifactId == story.Id && r.RelationshipType == RelationshipType.DerivedFrom)
            .Select(r => r.SourceArtifactId)
            .ToListAsync(cancellationToken);
        var existing = await ArtifactFingerprints.ExistingForParentAsync(
            _db, story.RequirementSourceId, ArtifactType.AcceptanceCriterion, story.Id, children, cancellationToken);
        var fresh = ArtifactFingerprints.Fresh(parsed.AcceptanceCriteria, ArtifactType.AcceptanceCriterion,
            c => ArtifactFingerprints.ForAcceptanceCriterion(story.Id, c.Given, c.When, c.Then), existing, out var skipped);
        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(
            _db, story.ProjectId, ArtifactType.AcceptanceCriterion, fresh.Count, cancellationToken);

        var acArtifacts = new List<Artifact>();

        for (var i = 0; i < fresh.Count; i++)
        {
            var item = fresh[i].Item;
            var acPayload = new AcceptanceCriterionPayload
            {
                Given = item.Given,
                When = item.When,
                Then = item.Then,
                Kind = item.Kind
            };

            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = story.ProjectId,
                ArtifactType = ArtifactType.AcceptanceCriterion,
                Code = codes[i],
                Title = item.Title,
                Status = ArtifactStatus.AiGenerated,
                Origin = ArtifactOrigin.Ai,
                RequirementSourceId = story.RequirementSourceId,
                DataJson = JsonSerializer.Serialize(acPayload),
                CurrentVersion = 1,
                CreatedByUserId = _currentUser.UserId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Artifacts.Add(artifact);
            _db.ArtifactVersions.Add(new ArtifactVersion
            {
                Id = Guid.NewGuid(),
                ArtifactId = artifact.Id,
                VersionNumber = 1,
                DataSnapshotJson = artifact.DataJson,
                Title = artifact.Title,
                Priority = artifact.Priority,
                Status = artifact.Status,
                ChangedByUserId = _currentUser.UserId,
                ChangedAt = now,
                Reason = "AI acceptance criteria generation",
                Origin = ArtifactOrigin.Ai
            });
            if (ArtifactFingerprints.TryRecord(artifact, fresh[i].Fingerprint, now) is { } fingerprint)
            {
                _db.ArtifactFingerprints.Add(fingerprint);
            }

            _db.ArtifactRelationships.Add(new ArtifactRelationship
            {
                Id = Guid.NewGuid(),
                SourceArtifactId = artifact.Id,
                TargetArtifactId = story.Id,
                RelationshipType = RelationshipType.DerivedFrom,
                CreatedAt = now
            });

            acArtifacts.Add(artifact);
        }

        GenerationSupport.AddExecutions(
            _db, story.ProjectId, "AcceptanceCriteriaGeneration", AcceptanceCriteriaPromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, story.Id.ToString(), rawResponse, now,
            acArtifacts.Select(a => a.Id).ToList());

        return (acArtifacts, skipped);
    }

    private class StoryAiResponse : IValidatableAiResponse
    {
        public List<UserStoryItem> UserStories { get; set; } = new();

        public void Validate(AiResponseValidator v) =>
            v.Items(UserStories, "userStories", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.Required(item.Persona, $"{path}.persona");
                v.Required(item.ValueStatement, $"{path}.valueStatement");
                v.OneOf(item.Priority, $"{path}.priority", AiVocabulary.Priorities, optional: true);
                v.CodesExist(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
                v.CodesRequired(item.RelatedRequirementCodes, $"{path}.relatedRequirementCodes");
            }, minItems: 1);
    }

    private class UserStoryItem
    {
        public string Title { get; set; } = string.Empty;
        public string Persona { get; set; } = string.Empty;
        public string ValueStatement { get; set; } = string.Empty;
        public string? Priority { get; set; }
        public List<string> RelatedRequirementCodes { get; set; } = new();
    }

    private class AcceptanceCriteriaAiResponse : IValidatableAiResponse
    {
        public List<AcceptanceCriterionItem> AcceptanceCriteria { get; set; } = new();

        public void Validate(AiResponseValidator v) =>
            v.Items(AcceptanceCriteria, "acceptanceCriteria", (item, path) =>
            {
                v.Required(item.Title, $"{path}.title");
                v.MaxLength(item.Title, 300, $"{path}.title");
                v.Required(item.Given, $"{path}.given");
                v.Required(item.When, $"{path}.when");
                v.Required(item.Then, $"{path}.then");
                v.OneOf(item.Kind, $"{path}.kind", AiVocabulary.AcceptanceCriterionKinds);
            }, minItems: 1);
    }

    private class AcceptanceCriterionItem
    {
        public string Title { get; set; } = string.Empty;
        public string Given { get; set; } = string.Empty;
        public string When { get; set; } = string.Empty;
        public string Then { get; set; } = string.Empty;
        public string Kind { get; set; } = "positive";
    }
}
