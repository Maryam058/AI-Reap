namespace AiReap.Domain.Enums;

public enum ProjectNotificationKind
{
    // The project entered a stage the recipient's role owns.
    Handover = 0,

    // A gated transition is waiting for the recipient (a Reviewer) to decide.
    ApprovalRequested = 1,

    // The recipient's transition request was rejected; the notice carries the reason.
    TransitionRejected = 2
}
