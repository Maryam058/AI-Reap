using System.Linq.Expressions;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

// A4a — which upstream artifacts new AI generation may build on. A Rejected artifact is a reviewer's
// recorded "no" (§24, a reason is required); lifecycle evidence, work rollup and conflict resolution
// already treat it as not there, and generation does the same: it is left out of every prompt, of the
// codes the model may cite, and so of the traceability links made from the response. Every other
// status - AiGenerated (awaiting review), Draft, UnderReview, Approved and later - stays an input, as
// before. Rejection is reversible (Rejected -> Draft/UnderReview), so a reworked artifact is an input
// again. Downstream artifacts generated before a rejection are not touched.
public static class GenerationInputs
{
    private static readonly Expression<Func<Artifact, bool>> Eligible = a => a.Status != ArtifactStatus.Rejected;
    private static readonly Func<Artifact, bool> EligibleCompiled = Eligible.Compile();

    public static bool IsEligible(Artifact artifact) => EligibleCompiled(artifact);

    // The artifacts of one type for one source that a generation prompt may build on, in code order.
    public static IQueryable<Artifact> For(IQueryable<Artifact> artifacts, Guid requirementSourceId, ArtifactType type) =>
        artifacts
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == type)
            .Where(Eligible)
            .OrderBy(a => a.Code);

    // R1 — a source whose FRs were all rejected has nothing left to build on: generating from its raw
    // text alone would override every one of those decisions. A source with no FRs at all is not this
    // case (stages there keep generating from the raw text, as before), nor is one with any FR left.
    public static async Task<bool> AllFunctionalRequirementsRejectedAsync(
        IAiReapDbContext db, Guid requirementSourceId, CancellationToken cancellationToken) =>
        await db.Artifacts.AnyAsync(
            a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == ArtifactType.FunctionalRequirement, cancellationToken)
        && !await For(db.Artifacts, requirementSourceId, ArtifactType.FunctionalRequirement).AnyAsync(cancellationToken);

    // The one wording for that refusal; `what` completes "there is nothing to ...".
    public static string AllRejectedMessage(string what) =>
        $"Every functional requirement for this source has been rejected, so there is nothing to {what}. " +
        "Rework a requirement (return it to Draft or Under Review) first.";

    // A direct generation request (HTTP 409) for such a source is refused before any AI call.
    public static async Task EnsureNotAllRejectedAsync(
        IAiReapDbContext db, Guid requirementSourceId, string what, CancellationToken cancellationToken)
    {
        if (await AllFunctionalRequirementsRejectedAsync(db, requirementSourceId, cancellationToken))
        {
            throw new ConflictException(AllRejectedMessage(what));
        }
    }

    // A direct request for test cases names one FR; a rejected one is refused rather than tested.
    public static void EnsureTestCaseTarget(Artifact functionalRequirement)
    {
        if (!IsEligible(functionalRequirement))
        {
            throw new ConflictException(
                $"{functionalRequirement.Code} has been rejected, so test cases are not generated for it. " +
                "Rework the requirement (return it to Draft or Under Review) first.");
        }
    }

    // Acceptance criteria refine one story: not for a rejected story, nor for a story whose linked FRs
    // (DerivedFrom) are all rejected. A story with no FR links is generated for, as before.
    public static bool CanGenerateAcceptanceCriteria(Artifact story, IReadOnlyCollection<Artifact> linkedFunctionalRequirements) =>
        IsEligible(story) && (linkedFunctionalRequirements.Count == 0 || linkedFunctionalRequirements.Any(IsEligible));

    // The FRs each story derives from, keyed by story id (UserStoryService links story -> FR as DerivedFrom).
    public static async Task<ILookup<Guid, Artifact>> LinkedFunctionalRequirementsAsync(
        IAiReapDbContext db, IReadOnlyCollection<Guid> storyIds, CancellationToken cancellationToken)
    {
        var ids = storyIds.ToList();
        var links = await db.ArtifactRelationships
            .Where(r => r.RelationshipType == RelationshipType.DerivedFrom && ids.Contains(r.SourceArtifactId))
            .Select(r => new { r.SourceArtifactId, r.TargetArtifactId })
            .ToListAsync(cancellationToken);
        var targetIds = links.Select(l => l.TargetArtifactId).Distinct().ToList();
        var frs = (await db.Artifacts
                .Where(a => targetIds.Contains(a.Id) && a.ArtifactType == ArtifactType.FunctionalRequirement)
                .ToListAsync(cancellationToken))
            .ToDictionary(a => a.Id);
        return links.Where(l => frs.ContainsKey(l.TargetArtifactId)).ToLookup(l => l.SourceArtifactId, l => frs[l.TargetArtifactId]);
    }

    // The direct request (POST .../generate-acceptance-criteria) for a story CanGenerateAcceptanceCriteria
    // rules out is refused, saying which of the two reasons applies.
    public static void EnsureAcceptanceCriteriaTarget(Artifact story, IReadOnlyCollection<Artifact> linkedFunctionalRequirements)
    {
        if (!IsEligible(story))
        {
            throw new ConflictException(
                $"{story.Code} has been rejected, so acceptance criteria are not generated for it. " +
                "Rework the story (return it to Draft or Under Review) first.");
        }

        if (linkedFunctionalRequirements.Count > 0 && !linkedFunctionalRequirements.Any(IsEligible))
        {
            var codes = string.Join(", ", linkedFunctionalRequirements.Select(fr => fr.Code).Order(StringComparer.Ordinal));
            throw new ConflictException(
                $"Every functional requirement {story.Code} derives from has been rejected ({codes}), so acceptance criteria " +
                "are not generated for it. Rework one of those requirements, or the story, first.");
        }
    }
}
