namespace AiReap.Application.Common;

// The request is valid but the current state doesn't allow it (wrong stage, already decided,
// evidence not met, lost a race). Mapped to 409 by the API layer.
public class ConflictException(string message) : InvalidOperationException(message);
