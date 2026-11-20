namespace AiReap.Application.Knowledge;

// Ai:Copilot:MaxQuestionChars for the Application layer (Infrastructure's CopilotSettings reads it).
public interface ICopilotSettings
{
    // The longest question the Copilot accepts, in UTF-16 code units (what string.Length and the
    // browser's value.length count). A longer question is rejected, never shortened.
    int MaxQuestionChars { get; }
}

public static class CopilotQuestion
{
    // Used when Ai:Copilot:MaxQuestionChars is absent, zero or negative.
    public const int DefaultMaxQuestionChars = 2_000;

    public static int EffectiveMax(int configured) => configured > 0 ? configured : DefaultMaxQuestionChars;

    // The validation message for a question over the limit, or null when it is within it. Names the
    // length and the limit only - never the question text.
    public static string? TooLong(string question, int maxChars) =>
        question.Length <= maxChars
            ? null
            : $"The question is {question.Length:N0} characters long; the Copilot accepts at most {maxChars:N0}. " +
              "Shorten the question and ask again - it is never shortened automatically.";
}
