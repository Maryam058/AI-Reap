namespace AiReap.Domain.Enums;

// Stored as int; values are fixed (the one-active-run index filters on Running and Staged).
public enum AiChunkRunStatus
{
    // Parts are pending or being processed.
    Running = 0,

    // Every part succeeded; waiting for Commit.
    Staged = 1,

    Committed = 2,

    // A part failed in a way retrying cannot fix (too large, or invalid output too many times).
    Failed = 3,

    // The source text or its context changed after Start; a new run is needed.
    Superseded = 4,

    Abandoned = 5
}

public enum AiChunkRunPartStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}

public enum AiChunkRunOperation
{
    Analyze = 0,
    Generate = 1
}
