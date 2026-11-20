namespace AiReap.Application.Sdlc;

// §28 — evidence-based SDLC progress. Each phase is complete only when every check passes; the
// checks read the project's real artifacts, so progress moves as work is done. The project's
// lifecycle Status (§5) stays a human decision - this only reports whether the work backs it.
public record SdlcCheck(string Label, bool Passed, string Detail);

public record SdlcPhaseProgress(string Key, string Label, bool Complete, IReadOnlyList<SdlcCheck> Checks);

public record ProjectSdlcProgress(Guid ProjectId, IReadOnlyList<SdlcPhaseProgress> Phases);
