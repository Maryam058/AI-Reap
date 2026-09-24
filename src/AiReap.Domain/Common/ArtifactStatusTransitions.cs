using AiReap.Domain.Enums;

namespace AiReap.Domain.Common;

// §22/§24 — the single source of truth for artifact status changes. Human decisions (PATCH
// .../status) may only follow the edges below; anything else is rejected rather than stored.
//
//   AiGenerated ─┬─> Draft ──> UnderReview ─┬─> Approved ──(system)──> Implemented ──(system)──> Verified
//                ├─> UnderReview            ├─> Rejected ──> Draft / UnderReview
//                ├─> Approved / Rejected    └─> Draft
//   Approved / Implemented / Verified ──> UnderReview (reopen);  Approved ──> Rejected (revoke)
//
// AiGenerated counts as "awaiting human review", so a reviewer may decide on it directly.
// Implemented/Verified are never set by a person: they're derived from approved downstream
// tasks/tests (ArtifactService auto-promotion) and go through CanSystemPromote instead.
public static class ArtifactStatusTransitions
{
    private static readonly Dictionary<ArtifactStatus, ArtifactStatus[]> HumanTransitions = new()
    {
        [ArtifactStatus.AiGenerated] = [ArtifactStatus.Draft, ArtifactStatus.UnderReview, ArtifactStatus.Approved, ArtifactStatus.Rejected],
        [ArtifactStatus.Draft] = [ArtifactStatus.UnderReview],
        [ArtifactStatus.UnderReview] = [ArtifactStatus.Approved, ArtifactStatus.Rejected, ArtifactStatus.Draft],
        [ArtifactStatus.Rejected] = [ArtifactStatus.Draft, ArtifactStatus.UnderReview],
        [ArtifactStatus.Approved] = [ArtifactStatus.UnderReview, ArtifactStatus.Rejected],
        [ArtifactStatus.Implemented] = [ArtifactStatus.UnderReview],
        [ArtifactStatus.Verified] = [ArtifactStatus.UnderReview],
    };

    public static bool CanHumanTransition(ArtifactStatus from, ArtifactStatus to) =>
        HumanTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static IReadOnlyList<ArtifactStatus> AllowedHumanTargets(ArtifactStatus from) =>
        HumanTransitions.TryGetValue(from, out var allowed) ? allowed : [];

    public static bool CanSystemPromote(ArtifactStatus from, ArtifactStatus to) =>
        (from, to) is (ArtifactStatus.Approved, ArtifactStatus.Implemented) or (ArtifactStatus.Implemented, ArtifactStatus.Verified);

    // Content in one of these states has been signed off; editing it must not keep the sign-off.
    public static bool IsApprovedOrLater(ArtifactStatus status) =>
        status is ArtifactStatus.Approved or ArtifactStatus.Implemented or ArtifactStatus.Verified;

    public static void EnsureHumanTransition(ArtifactStatus from, ArtifactStatus to)
    {
        if (!CanHumanTransition(from, to))
        {
            throw new InvalidArtifactStatusTransitionException(from, to);
        }
    }
}

public sealed class InvalidArtifactStatusTransitionException(ArtifactStatus from, ArtifactStatus to)
    : InvalidOperationException(
        $"An artifact cannot move from {from} to {to}. Allowed next statuses: " +
        (ArtifactStatusTransitions.AllowedHumanTargets(from) is { Count: > 0 } targets ? string.Join(", ", targets) : "none") + ".")
{
    public ArtifactStatus From { get; } = from;
    public ArtifactStatus To { get; } = to;
}
