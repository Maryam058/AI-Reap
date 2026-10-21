using System.Text.Json;
using System.Text.RegularExpressions;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Agents;

public class AgentOrchestrator : IAgentOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string AlreadyActiveMessage =
        "An agent run for this requirement source is already in progress. Finish it (approve/reject/retry) first.";

    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IReadOnlyList<IAgent> _agents;

    public AgentOrchestrator(IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess, IEnumerable<IAgent> agents)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _agents = agents.OrderBy(a => a.Kind).ToList();
    }

    public IReadOnlyList<AgentDefinitionResponse> GetDefinitions() =>
        _agents.Select((a, i) => new AgentDefinitionResponse(
            a.Kind, i, a.DisplayName, a.Responsibility, a.Inputs, a.Outputs, a.ApproverRoles,
            a.LifecycleStages, a.LifecycleContribution)).ToList();

    public async Task<AgentRunResponse> StartAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);

        if (await HasActiveRunAsync(requirementSourceId, cancellationToken))
        {
            throw new InvalidOperationException(AlreadyActiveMessage);
        }

        // Audit only: where the project was in its lifecycle when the run began. Agents may draft
        // ahead of it; the run never changes Project.Status.
        var projectStatus = await _db.Projects.Where(p => p.Id == source.ProjectId).Select(p => p.Status).FirstAsync(cancellationToken);

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            RequirementSourceId = source.Id,
            ProjectStatusAtStart = projectStatus,
            Status = AgentRunStatus.Running,
            StartedByUserId = _currentUser.UserId,
            StartedAt = DateTime.UtcNow,
            Stages = _agents.Select((a, i) => new AgentStageRun
            {
                Id = Guid.NewGuid(),
                Agent = a.Kind,
                Order = i,
                Status = AgentStageStatus.Pending
            }).ToList()
        };

        _db.AgentRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent start if another run is now active: the database's
            // one-active-run index rejected our insert. Anything else is a genuine failure.
            _db.AgentRuns.Remove(run);
            if (await HasActiveRunAsync(requirementSourceId, cancellationToken))
            {
                throw new InvalidOperationException(AlreadyActiveMessage);
            }

            throw;
        }

        await ExecuteStageAsync(run, run.Stages.OrderBy(s => s.Order).First(), cancellationToken);
        return await RespondAsync(run, cancellationToken);
    }

    public async Task<AgentRunResponse?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(run.ProjectId, cancellationToken);
        return await RespondAsync(run, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRunResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var runs = await _db.AgentRuns
            .Include(r => r.Stages)
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var hidden = await HiddenArtifactsAsync(projectId, cancellationToken);
        return runs.Select(r => ToResponse(r, hidden)).ToList();
    }

    public async Task<AgentRunSummaryResponse> GetSummaryAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var currentStatus = await _db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Status).FirstAsync(cancellationToken);
        var runs = await _db.AgentRuns
            .AsNoTracking()
            .Include(r => r.Stages)
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(cancellationToken);

        int Count(AgentRunStatus status) => runs.Count(r => r.Status == status);

        AgentRunStepSummary Step(AgentRun run, AgentStageRun step)
        {
            var agent = _agents.Single(a => a.Kind == step.Agent);
            var ahead = run.ProjectStatusAtStart is { } atStart
                && agent.LifecycleStages.Min(ProjectLifecyclePolicy.IndexOf) > ProjectLifecyclePolicy.IndexOf(atStart);
            return new AgentRunStepSummary(step.Agent, agent.DisplayName, agent.LifecycleStages, step.Status, agent.ApproverRoles,
                step.DecidedByUserId, step.DecidedAt, ahead);
        }

        var recent = runs.Take(10).Select(r =>
        {
            var ordered = r.Stages.OrderBy(s => s.Order).ToList();
            var current = ordered.FirstOrDefault(s => s.Status is AgentStageStatus.AwaitingApproval or AgentStageStatus.Running or AgentStageStatus.Failed);
            return new AgentRunSummaryItem(
                r.Id, r.RequirementSourceId, r.Status, r.StartedAt, r.CompletedAt, r.ProjectStatusAtStart,
                ordered.Count(s => s.Status == AgentStageStatus.Approved),
                ordered.Count(s => s.Status == AgentStageStatus.Rejected),
                ordered.Count,
                current is null ? null : Step(r, current),
                current?.Status is AgentStageStatus.AwaitingApproval or AgentStageStatus.Failed,
                ordered.Select(s => Step(r, s)).ToList());
        }).ToList();

        // Steps of every run, grouped under the lifecycle stage(s) their agent serves.
        var allSteps = runs.SelectMany(r => r.Stages).ToList();
        var byStage = ProjectLifecyclePolicy.Stages
            .Select(stage =>
            {
                var agents = _agents.Where(a => a.LifecycleStages.Contains(stage.Status)).ToList();
                var kinds = agents.Select(a => a.Kind).ToHashSet();
                var steps = allSteps.Where(s => kinds.Contains(s.Agent)).ToList();
                return new AgentStageActivity(
                    stage.Status, stage.Label, agents.Select(a => a.DisplayName).ToList(),
                    steps.Count(s => s.Status == AgentStageStatus.AwaitingApproval),
                    steps.Count(s => s.Status == AgentStageStatus.Approved),
                    steps.Count(s => s.Status == AgentStageStatus.Rejected),
                    steps.Count(s => s.Status == AgentStageStatus.Failed));
            })
            .ToList();

        return new AgentRunSummaryResponse(
            projectId, currentStatus, runs.Count,
            Active: Count(AgentRunStatus.Running) + Count(AgentRunStatus.AwaitingApproval),
            AwaitingDecision: Count(AgentRunStatus.AwaitingApproval),
            Failed: Count(AgentRunStatus.Failed),
            Completed: Count(AgentRunStatus.Completed),
            Rejected: Count(AgentRunStatus.Rejected),
            byStage,
            recent);
    }

    public async Task<AgentRunResponse> DecideAsync(Guid runId, bool approve, string? comment, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");
        await _projectAccess.EnsureMemberAsync(run.ProjectId, cancellationToken);

        var stage = run.Stages.SingleOrDefault(s => s.Status is AgentStageStatus.AwaitingApproval or AgentStageStatus.Failed)
            ?? throw new InvalidOperationException("This run is not awaiting a decision.");

        // A failed stage has no output to approve; the only decisions are retry or abandon (reject).
        if (approve && stage.Status == AgentStageStatus.Failed)
        {
            throw new InvalidOperationException("A failed step cannot be approved. Retry it, or reject to abandon the run.");
        }

        EnsureCanDecide(stage);

        stage.DecidedByUserId = _currentUser.UserId;
        stage.DecidedAt = DateTime.UtcNow;
        stage.DecisionComment = comment;

        if (!approve)
        {
            stage.Status = AgentStageStatus.Rejected;
            run.Status = AgentRunStatus.Rejected;
            run.CompletedAt = stage.DecidedAt;
            await SaveClaimAsync(cancellationToken);
            return await RespondAsync(run, cancellationToken);
        }

        stage.Status = AgentStageStatus.Approved;
        var next = run.Stages.OrderBy(s => s.Order).FirstOrDefault(s => s.Order > stage.Order);
        if (next is null)
        {
            run.Status = AgentRunStatus.Completed;
            run.CompletedAt = stage.DecidedAt;
            await SaveClaimAsync(cancellationToken);
            return await RespondAsync(run, cancellationToken);
        }

        run.Status = AgentRunStatus.Running;
        await SaveClaimAsync(cancellationToken); // claim the decision before running anything
        await ExecuteStageAsync(run, next, cancellationToken);
        return await RespondAsync(run, cancellationToken);
    }

    public async Task<AgentRunResponse> RetryAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");
        await _projectAccess.EnsureMemberAsync(run.ProjectId, cancellationToken);

        var stage = run.Stages.SingleOrDefault(s => s.Status == AgentStageStatus.Failed)
            ?? throw new InvalidOperationException("This run has no failed stage to retry.");

        EnsureCanDecide(stage);

        run.Status = AgentRunStatus.Running;
        await ExecuteStageAsync(run, stage, cancellationToken);
        return await RespondAsync(run, cancellationToken);
    }

    // Runs one agent step and parks the run at the human checkpoint (or Failed). This is the only
    // place a step executes, and it always ends in a non-Running state — the chain cannot
    // advance past a step without DecideAsync. It never touches Project.Status.
    private async Task ExecuteStageAsync(AgentRun run, AgentStageRun stage, CancellationToken cancellationToken)
    {
        var agent = _agents.Single(a => a.Kind == stage.Agent);

        stage.Status = AgentStageStatus.Running;
        stage.StartedAt = DateTime.UtcNow;
        stage.Error = null;
        await SaveClaimAsync(cancellationToken); // for a retry this is the claim: only one caller wins

        try
        {
            var output = await agent.RunAsync(new AgentContext(run.ProjectId, run.RequirementSourceId), cancellationToken);
            stage.OutputJson = JsonSerializer.Serialize(output, JsonOptions);
            stage.Status = AgentStageStatus.AwaitingApproval;
            run.Status = AgentRunStatus.AwaitingApproval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            stage.Status = AgentStageStatus.Failed;
            stage.Error = message;
            run.Status = AgentRunStatus.Failed;
        }

        stage.FinishedAt = DateTime.UtcNow;
        // The stage row's rowversion also guards this save: if someone else acted on the step while it
        // ran, this is refused with the same conflict as a lost claim. Not retried - the step has run.
        await SaveClaimAsync(cancellationToken);
    }

    private Task<bool> HasActiveRunAsync(Guid requirementSourceId, CancellationToken cancellationToken) =>
        _db.AgentRuns.AnyAsync(r =>
            r.RequirementSourceId == requirementSourceId &&
            (r.Status == AgentRunStatus.Running || r.Status == AgentRunStatus.AwaitingApproval || r.Status == AgentRunStatus.Failed),
            cancellationToken);

    // Saves a change that other callers may be racing for (stage row carries a rowversion).
    // Losing the race surfaces as the same "not in a state to decide" conflict a late caller gets.
    private async Task SaveClaimAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("This step was just acted on by someone else. Reload the run to see its current state.");
        }
    }

    private void EnsureCanDecide(AgentStageRun stage)
    {
        var agent = _agents.Single(a => a.Kind == stage.Agent);
        if (!agent.ApproverRoles.Any(_currentUser.IsInRole))
        {
            throw new UnauthorizedAccessException(
                $"The {agent.DisplayName} step can only be decided by: {string.Join(", ", agent.ApproverRoles)}.");
        }
    }

    private Task<AgentRun?> LoadRunAsync(Guid runId, CancellationToken cancellationToken) =>
        _db.AgentRuns.Include(r => r.Stages).FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    // §4 - a step's output lists the artifacts it produced (code, title) and findings that quote
    // artifact codes. A caller limited by ArtifactVisibility (a Developer) gets the same run with the
    // artifacts they may not see (unapproved requirements) left out, and with findings that name one
    // replaced by a count. S2 - the Requirements agent's findings are its analysis of the raw
    // requirement source (actors, capabilities, data elements, notes), so that caller gets none of
    // them. Step statuses, metrics and the approve/reject workflow are unchanged.
    private sealed record HiddenArtifacts(IReadOnlySet<Guid> Ids, Regex? Codes);

    private async Task<HiddenArtifacts?> HiddenArtifactsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!_currentUser.SeesOnlyApprovedArtifacts())
        {
            return null;
        }

        var hidden = (await _db.Artifacts.AsNoTracking()
                .Where(a => a.ProjectId == projectId)
                .Select(a => new { a.Id, a.Code, a.ArtifactType, a.Status })
                .ToListAsync(cancellationToken))
            .Where(a => !_currentUser.CanSee(a.ArtifactType, a.Status))
            .ToList();

        var codes = hidden.Count == 0
            ? null
            : new Regex($@"\b(?:{string.Join("|", hidden.Select(a => Regex.Escape(a.Code)))})\b");
        return new HiddenArtifacts(hidden.Select(a => a.Id).ToHashSet(), codes);
    }

    private async Task<AgentRunResponse> RespondAsync(AgentRun run, CancellationToken cancellationToken) =>
        ToResponse(run, await HiddenArtifactsAsync(run.ProjectId, cancellationToken));

    private AgentRunResponse ToResponse(AgentRun run, HiddenArtifacts? hidden) => new(
        run.Id, run.ProjectId, run.RequirementSourceId, run.Status, run.StartedByUserId, run.StartedAt, run.CompletedAt,
        run.Stages.OrderBy(s => s.Order).Select(s => ToResponse(s, hidden)).ToList(), run.ProjectStatusAtStart);

    private AgentStepResponse ToResponse(AgentStageRun stage, HiddenArtifacts? hidden)
    {
        var agent = _agents.Single(a => a.Kind == stage.Agent);
        var output = stage.Status is AgentStageStatus.Pending or AgentStageStatus.Running or AgentStageStatus.Failed
            ? null
            : JsonSerializer.Deserialize<AgentOutput>(stage.OutputJson, JsonOptions);
        if (output is not null && hidden is not null)
        {
            output = Redact(output, hidden, rawSourceFindings: stage.Agent == AgentKind.Requirements);
        }

        return new AgentStepResponse(
            stage.Id, stage.Agent, stage.Order, agent.DisplayName, stage.Status, output, stage.Error,
            agent.ApproverRoles, stage.StartedAt, stage.FinishedAt, stage.DecidedByUserId, stage.DecidedAt, stage.DecisionComment,
            agent.LifecycleStages);
    }

    private static AgentOutput Redact(AgentOutput output, HiddenArtifacts hidden, bool rawSourceFindings)
    {
        var findings = rawSourceFindings
            ? []
            : output.Findings.Where(f => hidden.Codes is null || !hidden.Codes.IsMatch(f)).ToList();
        var omitted = output.Findings.Count - findings.Count;
        if (omitted > 0)
        {
            findings.Add(rawSourceFindings
                ? $"{omitted} finding(s) not shown: they analyse the raw requirement source."
                : $"{omitted} finding(s) not shown: they refer to artifacts that are not approved yet.");
        }

        return output with
        {
            ProducedArtifacts = output.ProducedArtifacts.Where(a => !hidden.Ids.Contains(a.Id)).ToList(),
            Findings = findings
        };
    }
}
