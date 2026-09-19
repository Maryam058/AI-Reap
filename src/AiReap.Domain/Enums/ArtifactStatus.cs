namespace AiReap.Domain.Enums;

// §24 human-in-the-loop approval workflow, shared by every artifact type.
public enum ArtifactStatus
{
    AiGenerated,
    Draft,
    UnderReview,
    Approved,
    Rejected,
    Implemented,
    Verified
}
