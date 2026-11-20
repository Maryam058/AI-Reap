using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Lifecycle;

// Turns evaluated exit checks into what the current viewer is told: for each failing check, what
// blocks it, how to meet it, who can (FixRoles, whether the caller is one of them, which members
// are) and where. Read-only and advisory: pass/fail comes from LifecycleEvidenceService unchanged,
// and every endpoint still enforces its own rules.
//
// ArtifactVisibility applies here exactly as everywhere else: a blocking artifact the caller may not
// see is counted (HiddenCount) and never identified - no id, code, title or source - including when
// it is only the "related" side of another item's note.
public class ExitCheckGuidanceService
{
    public const int MaxItems = 200;

    private readonly IAiReapDbContext _db;
    private readonly IUserRoleDirectory _roles;
    private readonly ICurrentUser _currentUser;

    public ExitCheckGuidanceService(IAiReapDbContext db, IUserRoleDirectory roles, ICurrentUser currentUser)
    {
        _db = db;
        _roles = roles;
        _currentUser = currentUser;
    }

    // Loads the project's members and their roles once; the returned function builds views for any
    // number of that project's checks.
    public async Task<Func<CheckEvidence, ExitCheckView>> ForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var memberRoles = await ProjectMemberRoles.GetAsync(_db, _roles, projectId, cancellationToken);
        return check => View(check, memberRoles);
    }

    public async Task<IReadOnlyList<ExitCheckView>> ViewsAsync(Guid projectId, IEnumerable<CheckEvidence> checks, CancellationToken cancellationToken = default)
    {
        var view = await ForProjectAsync(projectId, cancellationToken);
        return checks.Select(view).ToList();
    }

    private ExitCheckView View(CheckEvidence evidence, IReadOnlyDictionary<string, IReadOnlyList<string>> memberRoles)
    {
        var check = evidence.Check;
        // A passed check needs nothing; an unknown key (no catalog entry) degrades to label + detail.
        if (check.Passed || !ExitCheckCatalog.TryGet(evidence.Key, out var guide))
        {
            return new ExitCheckView(check.Label, check.Passed, check.Detail);
        }

        var groups = evidence.Blockers
            .Where(b => guide.Reasons.ContainsKey(b.Reason))
            .Select(b => Group(b, guide.Reasons[b.Reason], memberRoles))
            .ToList();

        return new ExitCheckView(check.Label, check.Passed, check.Detail,
            new CheckGuidance(evidence.Key, guide.Kind, guide.HowTo, guide.DependsOn, groups));
    }

    private BlockerGroup Group(CheckBlocker blocker, ReasonGuide guide, IReadOnlyDictionary<string, IReadOnlyList<string>> memberRoles)
    {
        var visible = blocker.Items.Where(i => _currentUser.CanSee(i.Artifact)).ToList();
        var items = visible
            .Take(MaxItems)
            .Select(i => new BlockingItem(i.Artifact.Id, i.Artifact.Code, i.Artifact.Title, i.Artifact.ArtifactType, i.Artifact.Status,
                i.Artifact.RequirementSourceId, Note(i)))
            .ToList();

        var callerCanFix = guide.FixRoles.Any(_currentUser.IsInRole);
        var assignees = memberRoles
            .Where(m => m.Value.Any(guide.FixRoles.Contains))
            .Select(m => m.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new BlockerGroup(
            blocker.Reason, guide.Kind, guide.Title, guide.Instruction, guide.FixRoles, callerCanFix, CallerNote(guide, callerCanFix),
            assignees, guide.Destination, items, blocker.Items.Count, blocker.Items.Count - visible.Count);
    }

    private string? CallerNote(ReasonGuide guide, bool callerCanFix)
    {
        if (callerCanFix)
        {
            return null;
        }

        if (guide.AdminRecordsDontCount && _currentUser.IsInRole(Roles.Administrator))
        {
            return $"You may record this as an Administrator, but only a {string.Join(" or ", guide.FixRoles)}'s record counts as lifecycle evidence. " +
                   "Moving on without it is an Administrator override with a reason.";
        }

        return null;
    }

    // "edited since approval", "waiting on TASK-003, TASK-007", "conflicts with FR-002 and 1 item you
    // can't view": related artifacts are named only when the caller may see them.
    private string? Note(BlockerArtifact item)
    {
        if (item.Related is not { Count: > 0 } related)
        {
            return item.Note;
        }

        var codes = related.Where(_currentUser.CanSee).Select(a => a.Code).Distinct().OrderBy(c => c).ToList();
        var hidden = related.Where(a => !_currentUser.CanSee(a)).Select(a => a.Id).Distinct().Count();
        var parts = new List<string>();
        if (codes.Count > 0)
        {
            parts.Add(string.Join(", ", codes.Take(5)) + (codes.Count > 5 ? $" and {codes.Count - 5} more" : ""));
        }

        if (hidden > 0)
        {
            parts.Add(hidden == 1 ? "1 item you can't view" : $"{hidden} items you can't view");
        }

        var list = string.Join(" and ", parts);
        return string.IsNullOrEmpty(item.Note) ? list : $"{item.Note} {list}";
    }
}

// The active roles of a project's members (userId -> roles). Shared by lifecycle notifications and
// exit-check guidance, so "who holds this role on the project" is answered one way.
public static class ProjectMemberRoles
{
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(
        IAiReapDbContext db, IUserRoleDirectory roles, Guid projectId, CancellationToken cancellationToken)
    {
        var memberIds = await db.ProjectMembers.Where(m => m.ProjectId == projectId).Select(m => m.UserId).ToListAsync(cancellationToken);
        var active = await roles.GetActiveUserRolesAsync(memberIds, cancellationToken);
        return memberIds
            .Distinct()
            .Where(active.ContainsKey)
            .ToDictionary(id => id, id => active[id]);
    }
}
