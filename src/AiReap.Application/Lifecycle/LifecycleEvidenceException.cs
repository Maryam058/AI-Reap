using AiReap.Application.Common;

namespace AiReap.Application.Lifecycle;

// The stage's exit checks are not met. Carries the failing checks, with guidance for the caller, so
// they can see exactly what is missing and how to meet it. Mapped to 409 by the API layer, like every
// ConflictException.
public class LifecycleEvidenceException(string message, IReadOnlyList<ExitCheckView> failingChecks) : ConflictException(message)
{
    public IReadOnlyList<ExitCheckView> FailingChecks { get; } = failingChecks;
}
