using AiReap.Application.Common;
using AiReap.Application.Sdlc;

namespace AiReap.Application.Lifecycle;

// The stage's exit checks are not met. Carries the failing checks so the caller can show exactly
// what is missing. Mapped to 409 by the API layer, like every ConflictException.
public class LifecycleEvidenceException(string message, IReadOnlyList<SdlcCheck> failingChecks) : ConflictException(message)
{
    public IReadOnlyList<SdlcCheck> FailingChecks { get; } = failingChecks;
}
