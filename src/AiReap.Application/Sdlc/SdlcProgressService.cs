using AiReap.Application.Lifecycle;
using L = AiReap.Application.Lifecycle.LifecycleEvidenceService.Labels;

namespace AiReap.Application.Sdlc;

// §28 — the SDLC checklist the current UI reads (web/src/lib/sdlc.ts, phase keys below). It is a
// read-only regrouping of the lifecycle exit checks (LifecycleEvidenceService), not a lifecycle of
// its own: it has no state, drives no transition, and Project.Status stays the single lifecycle
// stage. Kept only until the UI moves to the eight lifecycle stages (lifecycle alignment phase 5).
public class SdlcProgressService : ISdlcProgressService
{
    private static readonly (string Key, string Label, string[] Checks)[] Phases =
    [
        ("gathering", "Requirements Gathering", [L.RawRequirements, L.Objectives, L.Stakeholders]),
        ("analysis", "Requirements Analysis", [L.AnalysisRun, L.QuestionsAnswered, L.FrsApproved, L.NfrsApproved]),
        ("validation", "Requirements Validation", [L.RulesApproved, L.StoriesApproved, L.CriteriaApproved, L.QualityRun, L.ConflictRun, L.ConflictsResolved]),
        ("design", "System Design", [L.DesignApproved, L.ApiApproved, L.DatabaseApproved]),
        ("development", "Development", [L.TasksApproved, L.FrsImplemented]),
        ("testing", "Testing", [L.TestsApproved, L.FrsVerified]),
        ("traceability", "Traceability", [L.FullyTraced]),
    ];

    private readonly ILifecycleEvidenceService _evidence;

    public SdlcProgressService(ILifecycleEvidenceService evidence)
    {
        _evidence = evidence;
    }

    public async Task<IReadOnlyList<SdlcPhaseProgress>> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var result = await GetManyAsync([projectId], cancellationToken);
        return result.Count == 0 ? [] : result[0].Phases;
    }

    public async Task<IReadOnlyList<ProjectSdlcProgress>> GetManyAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
    {
        var evidence = await _evidence.GetManyAsync(projectIds, cancellationToken);
        return evidence
            .Select(p =>
            {
                var checks = p.Value.SelectMany(s => s.Checks).GroupBy(c => c.Label).ToDictionary(g => g.Key, g => g.First());
                var phases = Phases
                    .Select(ph =>
                    {
                        var list = ph.Checks.Select(label => checks[label]).ToList();
                        return new SdlcPhaseProgress(ph.Key, ph.Label, list.All(c => c.Passed), list);
                    })
                    .ToList();
                return new ProjectSdlcProgress(p.Key, phases);
            })
            .ToList();
    }
}
