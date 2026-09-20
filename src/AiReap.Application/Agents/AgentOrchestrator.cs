using System.Text.Json;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
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
    private readonly IReadOnlyList<IAgent> _agents;

    public AgentOrchestrator(IAiReapDbContext db, ICurrentUser currentUser, IEnumerable<IAgent> agents)
    {
        _db = db;
        _currentUser = currentUser;
        _agents = agents.OrderBy(a => a.Kind).ToList();
    }

    public IReadOnlyList<AgentDefinitionResponse> GetDefinitions() =>
        _agents.Select((a, i) => new AgentDefinitionResponse(
            a.Kind, i, a.DisplayName, a.Responsibility, a.Inputs, a.Outputs, a.ApproverRoles)).ToList();

    public async Task<AgentRunResponse> StartAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");

        if (await HasActiveRunAsync(requirementSourceId, cancellationToken))
        {
            throw new InvalidOperationException(AlreadyActiveMessage);
        }

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            RequirementSourceId = source.Id,
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
        return ToResponse(run);
    }

    public async Task<AgentRunResponse?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        return run is null ? null : ToResponse(run);
    }

    public async Task<IReadOnlyList<AgentRunResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var runs = await _db.AgentRuns
            .Include(r => r.Stages)
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return runs.Select(ToResponse).ToList();
    }

    public async Task<AgentRunResponse> DecideAsync(Guid runId, bool approve, string? comment, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

        var stage = run.Stages.SingleOrDefault(s => s.Status is AgentStageStatus.AwaitingApproval or AgentStageStatus.Failed)
            ?? throw new InvalidOperationException("This run is not awaiting a decision.");

        // A failed stage has no output to approve; the only decisions are retry or abandon (reject).
        if (approve && stage.Status == AgentStageStatus.Failed)
        {
            throw new InvalidOperationException("A failed stage cannot be approved. Retry it, or reject to abandon the run.");
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
            return ToResponse(run);
        }

        stage.Status = AgentStageStatus.Approved;
        var next = run.Stages.OrderBy(s => s.Order).FirstOrDefault(s => s.Order > stage.Order);
        if (next is null)
        {
            run.Status = AgentRunStatus.Completed;
            run.CompletedAt = stage.DecidedAt;
            await SaveClaimAsync(cancellationToken);
            return ToResponse(run);
        }

        run.Status = AgentRunStatus.Running;
        await SaveClaimAsync(cancellationToken); // claim the decision before running anything
        await ExecuteStageAsync(run, next, cancellationToken);
        return ToResponse(run);
    }

    public async Task<AgentRunResponse> RetryAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

        var stage = run.Stages.SingleOrDefault(s => s.Status == AgentStageStatus.Failed)
            ?? throw new InvalidOperationException("This run has no failed stage to retry.");

        EnsureCanDecide(stage);

        run.Status = AgentRunStatus.Running;
        await ExecuteStageAsync(run, stage, cancellationToken);
        return ToResponse(run);
    }

    // Runs one agent and parks the run at the human checkpoint (or Failed). This is the only
    // place a stage executes, and it always ends in a non-Running state — the chain cannot
    // advance past a stage without DecideAsync.
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
        await _db.SaveChangesAsync(cancellationToken);
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
            throw new InvalidOperationException("This stage was just acted on by someone else. Reload the run to see its current state.");
        }
    }

    private void EnsureCanDecide(AgentStageRun stage)
    {
        var agent = _agents.Single(a => a.Kind == stage.Agent);
        if (!agent.ApproverRoles.Any(_currentUser.IsInRole))
        {
            throw new UnauthorizedAccessException(
                $"The {agent.DisplayName} stage can only be decided by: {string.Join(", ", agent.ApproverRoles)}.");
        }
    }

    private Task<AgentRun?> LoadRunAsync(Guid runId, CancellationToken cancellationToken) =>
        _db.AgentRuns.Include(r => r.Stages).FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    private AgentRunResponse ToResponse(AgentRun run) => new(
        run.Id, run.ProjectId, run.RequirementSourceId, run.Status, run.StartedByUserId, run.StartedAt, run.CompletedAt,
        run.Stages.OrderBy(s => s.Order).Select(ToResponse).ToList());

    private AgentStageResponse ToResponse(AgentStageRun stage)
    {
        var agent = _agents.Single(a => a.Kind == stage.Agent);
        var output = stage.Status is AgentStageStatus.Pending or AgentStageStatus.Running or AgentStageStatus.Failed
            ? null
            : JsonSerializer.Deserialize<AgentOutput>(stage.OutputJson, JsonOptions);

        return new AgentStageResponse(
            stage.Id, stage.Agent, stage.Order, agent.DisplayName, stage.Status, output, stage.Error,
            agent.ApproverRoles, stage.StartedAt, stage.FinishedAt, stage.DecidedByUserId, stage.DecidedAt, stage.DecisionComment);
    }
}
