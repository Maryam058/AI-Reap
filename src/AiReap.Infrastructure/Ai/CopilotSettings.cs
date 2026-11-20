using AiReap.Application.Knowledge;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Ai:Copilot settings (appsettings / user-secrets / Ai__Copilot__MaxQuestionChars).
public class CopilotOptions
{
    public const string SectionName = "Ai:Copilot";

    // The longest question the Copilot accepts; absent, zero or negative means the default (2,000).
    public int MaxQuestionChars { get; set; } = CopilotQuestion.DefaultMaxQuestionChars;
}

public sealed class CopilotSettings : ICopilotSettings
{
    private readonly IOptions<CopilotOptions> _options;

    public CopilotSettings(IOptions<CopilotOptions> options)
    {
        _options = options;
    }

    public int MaxQuestionChars => CopilotQuestion.EffectiveMax(_options.Value.MaxQuestionChars);
}
