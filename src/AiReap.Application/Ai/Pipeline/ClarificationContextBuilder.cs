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
    public const string Heading = "\n\nClarifications gathered so far:\n";

    public static async Task<string> BuildAsync(IAiReapDbContext db, Guid requirementSourceId, CancellationToken cancellationToken)
    {
        var items = await BuildItemsAsync(db, requirementSourceId, cancellationToken);
        return items.Count == 0 ? string.Empty : Heading + string.Concat(items);
    }

    // BuildAsync's list without its heading - one "- Q: ... / A: ..." entry per question, in code
    // order - so a stage that splits its source can show each part the clarifications relevant to
    // it (GenerationSupport.SharedContext) instead of all of them.
    public static Task<List<string>> BuildItemsAsync(IAiReapDbContext db, Guid requirementSourceId, CancellationToken cancellationToken) =>
        BuildItemsAsync(db, requirementSourceId, withCodes: false, cancellationToken);

    // withCodes: requirement generation only (P0-2) - each entry starts with its question's code, so
    // a requirement can cite the clarification its evidence or assumption comes from:
    // "- CQ-003 Q: ... / A: ...". Every other stage keeps the entries above, unchanged.
    public static async Task<List<string>> BuildItemsAsync(
        IAiReapDbContext db, Guid requirementSourceId, bool withCodes, CancellationToken cancellationToken)
    {
        var questions = await db.Artifacts
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == ArtifactType.ClarificationQuestion)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var items = new List<string>(questions.Count);

        // An open question no one can see (no verified source evidence, ClarificationVisibility) is left
        // out, so no requirement is generated as assuming it; an answer someone gave is always kept.
        ClarificationQuestionPayload PayloadOf(Domain.Entities.Artifact q) =>
            JsonSerializer.Deserialize<ClarificationQuestionPayload>(q.DataJson, options) ?? new ClarificationQuestionPayload();
        var open = questions.Where(q => PayloadOf(q).ClarificationStatus == "Open").ToList();
        var shownOpen = await ClarificationVisibility.ShownAsync(db, open, cancellationToken);

        foreach (var question in questions)
        {
            var payload = PayloadOf(question);
            if (payload.ClarificationStatus == "Open" && !shownOpen.ContainsKey(question.Id))
            {
                continue;
            }

            if (withCodes)
            {
                items.Add($"- {question.Code} Q: {payload.Question}\n" + (payload.ClarificationStatus == "Open"
                    ? $"  A: (not yet answered - anything that depends on it is assumed: cite {question.Code} in assumedClarifications)\n"
                    : $"  A: {payload.Answer}\n"));
                continue;
            }

            var sb = new StringBuilder($"- Q: {payload.Question}\n");
            sb.Append(payload.ClarificationStatus == "Open"
                ? "  A: (not yet answered - treat as still unresolved, propose an assumption if needed and flag it)\n"
                : $"  A: {payload.Answer}\n");
            items.Add(sb.ToString());
        }

        return items;
    }
}
