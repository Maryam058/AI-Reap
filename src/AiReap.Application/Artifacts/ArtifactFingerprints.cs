using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Artifacts;

// One deterministic fingerprint per AI-created requirement or clarification question, so a retry,
// a re-run or two concurrent runs never save the same artifact twice for one requirement source.
// Each content field is normalized on its own (AiResultMerge.Key: case, punctuation, leading
// article and plural "s" ignored) and kept apart, then hashed (SHA-256, hex):
//   functional requirement      actor, title, preconditions, inputs, processing, expected result
//   non-functional requirement  category, title, description, target value
//   clarification question      question
// - the same fields RequirementGenerationService.Merge and RequirementAnalysisService.Merge already
// treat as "the same item". Two requirements that differ in any field are both kept. The scope is
// (requirement source, artifact type): the same content under another source or type is distinct.
// Normal and batched runs use these functions and nothing else.
//
// G2 — the downstream AI-generated types, so re-running a stage with the same logical output adds
// nothing. Fields, normalized and hashed the same way:
//   user story            persona, title, value statement
//   business rule         title, statement
//   solution design       title, architecture overview
//   data entity           entity name (title) - one entity per name in a source's design
//   API specification     HTTP method and route, with route parameters treated alike ("{id}" = "{roomId}")
//   implementation task   title, task type, description
//   acceptance criterion  its user story, given, when, then          (scoped to that story)
//   test case             its functional requirement, preconditions, steps, expected result (scoped to that FR)
// This is exact matching after normalization, not semantic deduplication: a paraphrased item (other
// wording for the same thing) gets a different fingerprint and is saved as a new artifact. A match
// is only ever skipped - an existing artifact (reviewed, edited or rejected) is never replaced,
// deleted or changed - and an existing artifact's current content counts as well as the content it
// was created with, so neither a human edit nor a rejection is undone by a re-run.
public static class ArtifactFingerprints
{
    public static string ForFunctional(string? actor, string? title, string? preconditions, string? inputs, string? processing, string? expectedResult) =>
        Hash("FR1", actor, title, preconditions, inputs, processing, expectedResult);

    public static string ForNonFunctional(string? category, string? title, string? description, string? targetValue) =>
        Hash("NFR1", category, title, description, targetValue);

    public static string ForClarification(string? question) => Hash("CQ1", question);

    public static string ForUserStory(string? persona, string? title, string? valueStatement) =>
        Hash("US1", persona, title, valueStatement);

    public static string ForBusinessRule(string? title, string? statement) => Hash("BR1", title, statement);

    public static string ForSolutionDesign(string? title, string? architectureOverview) => Hash("DA1", title, architectureOverview);

    public static string ForDataEntity(string? name) => Hash("DE1", name);

    public static string ForApiSpecification(string? method, string? route) => Hash("API1", method, RouteShape(route));

    public static string ForImplementationTask(string? title, string? taskType, string? description) =>
        Hash("TASK1", title, taskType, description);

    public static string ForAcceptanceCriterion(Guid userStoryId, string? given, string? when, string? then) =>
        Hash("AC1", userStoryId.ToString("N"), given, when, then);

    public static string ForTestCase(Guid functionalRequirementId, string? preconditions, IEnumerable<string>? steps, string? expectedResult) =>
        Hash("TC1", functionalRequirementId.ToString("N"), preconditions, string.Join("\n", steps ?? []), expectedResult);

    // "/API/Rooms/{roomId}/" and "/api/rooms/:id" are one route shape: lower case, no trailing slash,
    // every route parameter the same placeholder.
    private static string RouteShape(string? route)
    {
        var shape = Regex.Replace((route ?? string.Empty).Trim().ToLowerInvariant(), @"\{[^}]*\}|:[a-z_][a-z0-9_]*", "param");
        return shape.Length > 1 ? shape.TrimEnd('/') : shape;
    }

    public static readonly IReadOnlyList<ArtifactType> GeneratedTypes =
        [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement, ArtifactType.ClarificationQuestion];

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // The fingerprint of an artifact's current content (for artifacts with no recorded fingerprint,
    // i.e. created before fingerprints existed, and for edited ones); null for other types - and for
    // acceptance criteria and test cases, whose fingerprint needs their parent (ForCurrentChildContent).
    public static string? ForCurrentContent(Artifact artifact)
    {
        try
        {
            switch (artifact.ArtifactType)
            {
                case ArtifactType.FunctionalRequirement:
                    var fr = JsonSerializer.Deserialize<FunctionalRequirementPayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForFunctional(fr.Actor, artifact.Title, fr.Preconditions, fr.Inputs, fr.Processing, fr.ExpectedResult);
                case ArtifactType.NonFunctionalRequirement:
                    var nfr = JsonSerializer.Deserialize<NonFunctionalRequirementPayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForNonFunctional(nfr.Category, artifact.Title, nfr.Description, nfr.TargetValue);
                case ArtifactType.ClarificationQuestion:
                    var cq = JsonSerializer.Deserialize<ClarificationQuestionPayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForClarification(cq.Question);
                case ArtifactType.UserStory:
                    var us = JsonSerializer.Deserialize<UserStoryPayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForUserStory(us.Persona, artifact.Title, us.ValueStatement);
                case ArtifactType.BusinessRule:
                    var br = JsonSerializer.Deserialize<BusinessRulePayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForBusinessRule(artifact.Title, br.Statement);
                case ArtifactType.DesignArtifact:
                    var da = JsonSerializer.Deserialize<DesignArtifactPayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForSolutionDesign(artifact.Title, da.ArchitectureOverview);
                case ArtifactType.DataEntity:
                    return ForDataEntity(artifact.Title);
                case ArtifactType.ApiSpecification:
                    var api = JsonSerializer.Deserialize<ApiSpecificationPayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForApiSpecification(api.Method, api.Route);
                case ArtifactType.ImplementationTask:
                    var task = JsonSerializer.Deserialize<ImplementationTaskPayload>(artifact.DataJson, ReadOptions) ?? new();
                    return ForImplementationTask(artifact.Title, task.TaskType, task.Description);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The current-content fingerprint of an acceptance criterion or test case under a given parent
    // (its user story, or its functional requirement); null for any other type.
    public static string? ForCurrentChildContent(Artifact child, Guid parentId)
    {
        try
        {
            switch (child.ArtifactType)
            {
                case ArtifactType.AcceptanceCriterion:
                    var ac = JsonSerializer.Deserialize<AcceptanceCriterionPayload>(child.DataJson, ReadOptions) ?? new();
                    return ForAcceptanceCriterion(parentId, ac.Given, ac.When, ac.Then);
                case ArtifactType.TestCase:
                    var tc = JsonSerializer.Deserialize<TestCasePayload>(child.DataJson, ReadOptions) ?? new();
                    return ForTestCase(parentId, tc.Preconditions, tc.Steps, tc.ExpectedResult);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What already exists for the source, as (type, fingerprint): every recorded fingerprint, plus
    // the current content of every artifact of those types (so older artifacts without a recorded
    // fingerprint, and edited ones, count too). Read without tracking.
    public static async Task<HashSet<(ArtifactType Type, string Fingerprint)>> ExistingAsync(
        IAiReapDbContext db, Guid requirementSourceId, IReadOnlyCollection<ArtifactType> types, CancellationToken cancellationToken)
    {
        var recorded = await db.ArtifactFingerprints.AsNoTracking()
            .Where(f => f.RequirementSourceId == requirementSourceId && types.Contains(f.ArtifactType))
            .Select(f => new { f.ArtifactType, f.Fingerprint })
            .ToListAsync(cancellationToken);
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.RequirementSourceId == requirementSourceId && types.Contains(a.ArtifactType))
            .ToListAsync(cancellationToken);

        var existing = recorded.Select(f => (f.ArtifactType, f.Fingerprint)).ToHashSet();
        foreach (var artifact in artifacts)
        {
            if (ForCurrentContent(artifact) is { } fingerprint)
            {
                existing.Add((artifact.ArtifactType, fingerprint));
            }
        }

        return existing;
    }

    // ExistingAsync for the children of one parent - the acceptance criteria of a user story, or the
    // test cases of a functional requirement: the fingerprints recorded for the children's source
    // (each already carries its parent, so other parents' children never match), plus the current
    // content of the parent's existing children. requirementSourceId is null when the parent belongs
    // to no requirement source; only current content is compared then.
    public static async Task<HashSet<(ArtifactType Type, string Fingerprint)>> ExistingForParentAsync(
        IAiReapDbContext db, Guid? requirementSourceId, ArtifactType childType, Guid parentId, IReadOnlyCollection<Guid> childIds,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<(ArtifactType Type, string Fingerprint)>();
        if (requirementSourceId is { } sourceId)
        {
            var recorded = await db.ArtifactFingerprints.AsNoTracking()
                .Where(f => f.RequirementSourceId == sourceId && f.ArtifactType == childType)
                .Select(f => f.Fingerprint)
                .ToListAsync(cancellationToken);
            existing.UnionWith(recorded.Select(f => (childType, f)));
        }

        var ids = childIds.ToList();
        var children = await db.Artifacts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.ArtifactType == childType)
            .ToListAsync(cancellationToken);
        foreach (var child in children)
        {
            if (ForCurrentChildContent(child, parentId) is { } fingerprint)
            {
                existing.Add((childType, fingerprint));
            }
        }

        return existing;
    }

    // The items of one AI response that are not already saved (nor repeated earlier in the same
    // response), in response order, each with its fingerprint; the rest are counted in skipped.
    // `existing` is updated, so it also holds the new items' fingerprints afterwards.
    public static List<(T Item, string Fingerprint)> Fresh<T>(
        IEnumerable<T> items, ArtifactType type, Func<T, string> fingerprint,
        HashSet<(ArtifactType Type, string Fingerprint)> existing, out int skipped)
    {
        var fresh = new List<(T Item, string Fingerprint)>();
        skipped = 0;
        foreach (var item in items)
        {
            var value = fingerprint(item);
            if (existing.Add((type, value))) fresh.Add((item, value));
            else skipped++;
        }

        return fresh;
    }

    // Record, for an artifact that may belong to no requirement source: no row then (the fingerprint
    // table is keyed by source), and duplicates are caught from current content alone.
    public static ArtifactFingerprint? TryRecord(Artifact artifact, string fingerprint, DateTime now) =>
        artifact.RequirementSourceId is null ? null : Record(artifact, fingerprint, now);

    public static ArtifactFingerprint Record(Artifact artifact, string fingerprint, DateTime now) => new()
    {
        ArtifactId = artifact.Id,
        RequirementSourceId = artifact.RequirementSourceId
            ?? throw new InvalidOperationException("A fingerprinted artifact must belong to a requirement source."),
        ArtifactType = artifact.ArtifactType,
        Fingerprint = fingerprint,
        CreatedAt = now
    };

    private static string Hash(string kind, params string?[] fields)
    {
        var text = kind + "\u001e" + string.Join("\u001f", fields.Select(f => AiResultMerge.Key(f ?? string.Empty)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
