namespace AiReap.Application.Ai;

// Thrown when an AI response fails to parse into the expected structured shape, or parses but
// fails semantic validation (IValidatableAiResponse). Callers must never persist a
// partially-parsed or best-effort result (REAP-005) — this exception is the signal to reject the
// whole generation and surface it to the user.
public class AiOutputValidationException : Exception
{
    public AiOutputValidationException(string message, Exception? inner = null) : base(message, inner)
    {
        Errors = [message];
    }

    public AiOutputValidationException(string message, IReadOnlyList<string> errors, bool repairable = true) : base(message)
    {
        Errors = errors;
        Repairable = repairable;
    }

    // Individual problems, suitable for feeding back to the model in a repair attempt and for the
    // API error response. Never contains prompt text or credentials.
    public IReadOnlyList<string> Errors { get; }

    // False when asking the model again with the errors can't help (e.g. the response was cut off
    // at the output-token limit), so no repair attempt is made.
    public bool Repairable { get; init; } = true;
}
