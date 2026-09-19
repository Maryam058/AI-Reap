using System.Text;
using System.Text.Json;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Persistence;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

// Every generation stage past the initial analysis re-reads the retained raw source (§6)
// plus whatever clarification Q&A exists for it, rather than relying on a separately
// persisted "analysis result" — there isn't one; only the questions it produced are stored.
public static class ClarificationContextBuilder
{
    public static async Task<string> BuildAsync(IAiReapDbContext db, Guid requirementSourceId, CancellationToken cancellationToken)
    {
        var questions = await db.Artifacts
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == ArtifactType.ClarificationQuestion)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        if (questions.Count == 0)
        {
            return string.Empty;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var sb = new StringBuilder("\n\nClarifications gathered so far:\n");

        foreach (var question in questions)
        {
            var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(question.DataJson, options)
                ?? new ClarificationQuestionPayload();

            sb.Append($"- Q: {payload.Question}\n");
            sb.Append(payload.ClarificationStatus == "Open"
                ? "  A: (not yet answered - treat as still unresolved, propose an assumption if needed and flag it)\n"
                : $"  A: {payload.Answer}\n");
        }

        return sb.ToString();
    }
}
