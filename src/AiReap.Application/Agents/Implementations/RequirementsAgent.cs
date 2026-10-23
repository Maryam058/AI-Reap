using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;

namespace AiReap.Application.Agents.Implementations;

// Stage 1 — understands the raw requirement and asks what's missing. Deliberately stops before
// generating requirements: the human's answers to its questions feed the next stage.
public class RequirementsAgent : IAgent
{
    private readonly IRequirementAnalysisService _analysis;
    private readonly IAiReapDbContext _db;

    public RequirementsAgent(IRequirementAnalysisService analysis, IAiReapDbContext db)
    {
        _analysis = analysis;
        _db = db;
    }

    public AgentKind Kind => AgentKind.Requirements;
    public string DisplayName => "Requirements Agent";
    public string Responsibility => "Analyses the raw requirement source (actors, capabilities, data) and raises clarification questions for anything missing.";
    public string Inputs => "Requirement source (raw text, retained verbatim).";
    public string Outputs => "Clarification questions (ClarificationQuestion artifacts).";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.BusinessAnalyst);
    public IReadOnlyList<ProjectStatus> LifecycleStages => [ProjectStatus.RequirementsGathering];
    public string LifecycleContribution => "Works in Requirements Gathering: analyses the raw source and raises clarification questions, preparing the evidence Analysis needs (analysis run, questions answered).";

    public async Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        // The stored analysis is reused (no AI call) whenever the source has one - including one that
        // raised no questions; only an explicit regenerate in the Requirements workspace analyses again.
        var findings = new List<string>();
        var result = await _analysis.AnalyzeAsync(context.RequirementSourceId, cancellationToken);
        if (result.Actors.Count > 0) findings.Add($"Actors: {string.Join(", ", result.Actors)}");
        if (result.Capabilities.Count > 0) findings.Add($"Capabilities: {string.Join(", ", result.Capabilities)}");
        if (result.DataElements.Count > 0) findings.Add($"Data elements: {string.Join(", ", result.DataElements)}");
        findings.AddRange(result.Notes);
        findings.AddRange(result.StaleReasons ?? []);
        if (result.Coverage is { Status: AnalysisCoverageReport.Incomplete } coverage)
        {
            findings.Add($"Incomplete analysis - not sent to the AI: {string.Join("; ", coverage.MissingSections)}.");
        }

        var existing = await AgentSupport.LoadAsync(_db, context.RequirementSourceId, ArtifactType.ClarificationQuestion, cancellationToken);
        var summary = result.Reused
            ? $"Reused the stored analysis of this source ({existing.Count} clarification question(s)); the AI was not called again."
            : $"Analysed the requirement and raised {existing.Count} clarification question(s)" +
              (result.RejectedCandidates is { Count: > 0 } rejected ? $"; {rejected.Count} proposed question(s) were rejected by validation." : ".");

        var open = AgentSupport.OpenClarifications(existing);
        return new AgentOutput(
            summary,
            new Dictionary<string, int> { ["clarificationQuestions"] = existing.Count, ["openQuestions"] = open },
            existing.Select(AgentSupport.ToRef).ToList(),
            findings,
            open > 0
                ? $"{open} question(s) are still open. Answer them (Requirements workspace) before approving — the Analysis Agent writes requirements using your answers."
                : "All questions are answered. Approve to let the Analysis Agent generate requirements.");
    }
}
