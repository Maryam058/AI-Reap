namespace AiReap.Domain.Enums;

// A direct transition is Completed as soon as it is recorded. A gated one (Design -> Approved,
// Testing -> Completed) starts Pending and ends Completed, Rejected or Withdrawn. Stored as int;
// the "one pending request per project" index filters on Pending = 0.
public enum StageTransitionState
{
    Pending = 0,
    Completed = 1,
    Rejected = 2,
    Withdrawn = 3
}
