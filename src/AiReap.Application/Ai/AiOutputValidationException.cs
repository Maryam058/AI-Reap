namespace AiReap.Application.Ai;

// Thrown when an AI response fails to parse into the expected structured shape.
// Callers must never persist a partially-parsed or best-effort result (REAP-005) —
// this exception is the signal to reject the whole generation and surface it to the user.
public class AiOutputValidationException : Exception
{
    public AiOutputValidationException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
