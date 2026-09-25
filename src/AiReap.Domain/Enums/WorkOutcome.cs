namespace AiReap.Domain.Enums;

// Implementation and verification evidence, recorded by people against an approved artifact.
// Approving a task or a test case only approves the plan; these record that the work happened.
// Task outcomes apply to ImplementationTask artifacts, test outcomes to TestCase artifacts.
public enum WorkOutcome
{
    TaskCompleted = 0,
    TaskReopened = 1,
    TestPassed = 2,
    TestFailed = 3,
    TestBlocked = 4
}
