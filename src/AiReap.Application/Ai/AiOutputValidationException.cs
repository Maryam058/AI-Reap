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

    // True only when this failure is the result of the one automatic repair attempt
    // (GenerationSupport sets it): the API may then say a correction was tried. A failure raised by
    // the provider client itself (truncation, an empty or malformed envelope) never had one.
    public bool RepairAttempted { get; internal set; }
}

// The response was cut off before it finished - at the max_tokens output limit (thinking included)
// or at the model's context window - so it is incomplete by definition. Never repairable with the
// same request; staged requirement generation (GenerationSupport) reacts to it by sending the same
// source text in smaller parts, never by accepting the partial output.
public sealed class AiOutputTruncatedException : AiOutputValidationException
{
    public AiOutputTruncatedException(string message, IReadOnlyList<string> errors, string stopReason)
        : base(message, errors, repairable: false)
    {
        StopReason = stopReason;
    }

    // "max_tokens" or "model_context_window_exceeded".
    public string StopReason { get; }
}

// Staged requirement generation (GenerationSupport, StagedPartCoverage): one or more parts define
// requirement IDs, the AI returned no requirements for them, and no other generated requirement quotes
// their text - while requirements of the same ID family were generated - so the result would silently
// miss them. Never repairable and never retryable as such: the cause may be the model leaving them out
// by chance, or IDs that are not requirements to generate (removed, out of scope), which fails the same
// way every time. Nothing is saved.
public sealed class AiStagedPartIncompleteException : AiOutputValidationException
{
    public AiStagedPartIncompleteException(string message, IReadOnlyList<string> errors,
        IReadOnlyList<StagedPartGap> parts, int requestsSent)
        : base(message, errors, repairable: false)
    {
        Parts = parts;
        RequestsSent = requestsSent;
    }

    // The parts whose requirements are missing, numbered as the run ended.
    public IReadOnlyList<StagedPartGap> Parts { get; }

    // AI requests this run sent (first attempts, repair attempts and rate-limit re-sends). Retries made
    // by the transport after a transient error (ResilientAiChatClient) are not visible here.
    public int RequestsSent { get; }
}

public sealed record StagedPartGap(int Part, int PartCount, IReadOnlyList<string> MissingIds);
