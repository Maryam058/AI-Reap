using System.Text.Json;

namespace AiReap.Application.Lifecycle;

// F3/D2 — what an Administrator's override at submission was given over, as stable failure
// identities rather than check labels and details (which can stay the same while the failing
// artifacts change: counts, truncated listings, codes hidden from the viewer).
//
// One identity per blocking item of a failing check: the check's key, the blocker reason, the
// blocking artifact's id, the ids of the artifacts it relates to (the other side of a conflict, the
// tasks or tests it waits on) and its note. A note is server-generated from codes (the missing trace
// links, the other evidence codes, "edited since approval"), the same whatever order rows are read
// in, never truncated, never viewer-dependent and never quote text; it is part of the identity only to tell apart failures of the same artifact
// and reason, so it can narrow what is covered but never widen it. A blocker with no artifacts (a
// check that is not artifact-specific, e.g. "not run") is identified by key and reason.
//
// Fail-safe: a failing check without a key, without blockers, or with a blocking item that has no
// artifact id cannot be identified, and is never covered. Snapshots written before this (no
// identities), or unreadable ones, authorize nothing.
public static class OverrideScope
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // The transition's evidence snapshot entry: the plain SdlcCheck fields, which is all the history
    // returns (extra fields are ignored when read as SdlcCheck), plus the check key and, for a
    // failing check, its failure identities. The ids never leave the server.
    private sealed record SnapshotCheck(string Label, bool Passed, string Detail, string? Key = null, IReadOnlyList<string>? Failures = null);

    public static string Snapshot(IEnumerable<CheckEvidence> checks) =>
        JsonSerializer.Serialize(checks.Select(c => new SnapshotCheck(
            c.Check.Label, c.Check.Passed, c.Check.Detail, c.Key,
            c.Check.Passed ? null : FailureIds(c)?.Order(StringComparer.Ordinal).ToList() ?? [])).ToList(), JsonOptions);

    // The failure identities a snapshot authorizes: those of its failing checks. Empty for a snapshot
    // without identities (made before them) or one that cannot be read.
    public static IReadOnlySet<string> Authorized(string snapshotJson)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<List<SnapshotCheck?>>(snapshotJson, JsonOptions) ?? [];
            return snapshot
                .Where(c => c is { Passed: false, Failures: not null })
                .SelectMany(c => c!.Failures!)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    // A failing check is covered only when it can be identified and every one of its failures is authorized.
    public static bool Covers(IReadOnlySet<string> authorized, CheckEvidence check) =>
        authorized.Count > 0 && FailureIds(check) is { Count: > 0 } ids && ids.All(authorized.Contains);

    // The check's failure identities, or null when they cannot all be established.
    public static IReadOnlySet<string>? FailureIds(CheckEvidence check)
    {
        if (string.IsNullOrWhiteSpace(check.Key) || check.Blockers.Count == 0)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var blocker in check.Blockers)
        {
            if (string.IsNullOrWhiteSpace(blocker.Reason))
            {
                return null;
            }

            if (blocker.Items.Count == 0)
            {
                ids.Add(Id(check.Key, blocker.Reason));
                continue;
            }

            foreach (var item in blocker.Items)
            {
                if (item.Artifact is null || item.Artifact.Id == Guid.Empty || item.Related?.Any(r => r is null || r.Id == Guid.Empty) == true)
                {
                    return null;
                }

                var related = (item.Related ?? []).Select(r => r.Id.ToString("N")).Distinct().Order(StringComparer.Ordinal);
                ids.Add(Id(check.Key, blocker.Reason, item.Artifact.Id.ToString("N"), string.Join(",", related), item.Note?.Trim() ?? ""));
            }
        }

        return ids;
    }

    private static string Id(params string[] parts) => JsonSerializer.Serialize(parts);
}
