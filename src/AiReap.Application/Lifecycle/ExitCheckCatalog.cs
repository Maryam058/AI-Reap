using AiReap.Domain.Common;

namespace AiReap.Application.Lifecycle;

// Stable identifiers of the lifecycle exit checks (LifecycleEvidenceService, plus the Testing sign-off
// ProjectLifecycleService adds). Labels are display text; these keys are what guidance is keyed on.
public static class ExitCheckKeys
{
    public const string BusinessProblem = "draft.business-problem";
    public const string Objectives = "project.objectives";
    public const string Scope = "draft.scope";
    public const string BusinessAnalystMember = "draft.ba-member";

    public const string RawRequirements = "gathering.raw-requirements";
    public const string Stakeholders = "gathering.stakeholders";

    public const string AnalysisRun = "analysis.run";
    public const string QuestionsAnswered = "analysis.questions";
    public const string FrsApproved = "analysis.frs-approved";
    public const string NfrsApproved = "analysis.nfrs-approved";
    public const string RulesApproved = "analysis.rules-approved";
    public const string StoriesApproved = "analysis.stories-approved";
    public const string CriteriaApproved = "analysis.criteria-approved";
    public const string QualityRun = "analysis.quality-run";
    public const string ConflictRun = "analysis.conflict-run";
    public const string ConflictsResolved = "conflicts.resolved";
    public const string StoryChain = "analysis.chain";

    public const string DesignApproved = "design.solution-approved";
    public const string ApiApproved = "design.api-approved";
    public const string DatabaseApproved = "design.db-approved";
    public const string DeveloperReview = "design.developer-review";
    public const string DesignNotices = "design.impact-notices";
    public const string Baseline = "design.baseline";

    public const string TasksApproved = "approved.tasks-approved";
    public const string EveryFrHasTask = "approved.fr-has-task";

    public const string TasksCompleted = "impl.tasks-completed";
    public const string FrsImplemented = "impl.frs-implemented";

    public const string TestsApproved = "testing.tests-approved";
    public const string EveryFrHasApprovedTest = "testing.fr-has-approved-test";
    public const string TestsPassed = "testing.tests-passed";
    public const string FrsVerified = "testing.frs-verified";
    public const string FullyTraced = "testing.fully-traced";
    public const string TestingNotices = "testing.impact-notices";
    public const string QaSignOff = "testing.qa-signoff";
}

// How to meet one blocker reason of a check.
//   FixRoles  roles whose action counts as lifecycle evidence. Never wider than the roles the
//             endpoint allows (Endpoint, "Controller.Action"; ExitCheckGuidanceTests checks this
//             against the real [Authorize] attributes), and narrower where evidence is: only a
//             Developer's work record completes a task, only QA's records a test result, only a
//             review made as a Developer counts as the Developer review.
//   AdminRecordsDontCount  an Administrator may call the endpoint, but the record is not evidence
//             (LifecycleEvidenceService.LatestOutcomes / the Developer-review rule).
public sealed record ReasonGuide(
    string Kind,
    string Title,
    string Instruction,
    IReadOnlyList<string> FixRoles,
    CheckDestination Destination,
    string? Endpoint,
    bool AdminRecordsDontCount = false);

public sealed record CheckGuide(string Kind, string HowTo, IReadOnlyDictionary<string, ReasonGuide> Reasons, IReadOnlyList<string> DependsOn);

// §5 — "how to meet this" for every lifecycle exit check: what it takes, who can do it and where.
// Read-only guidance: it never changes whether a check passes, and the endpoints enforce every rule.
public static class ExitCheckCatalog
{
    private static readonly string[] Writers = [Roles.BusinessAnalyst, Roles.Administrator];
    private static readonly string[] Approvers = [Roles.Reviewer, Roles.BusinessAnalyst, Roles.Administrator];
    private static readonly string[] QualityRunners = [Roles.BusinessAnalyst, Roles.Reviewer, Roles.Administrator];
    private static readonly string[] TestWriters = [Roles.QA, Roles.BusinessAnalyst, Roles.Administrator];
    private static readonly string[] NoticeAcknowledgers = [Roles.BusinessAnalyst, Roles.Reviewer, Roles.Developer, Roles.QA, Roles.Administrator];

    private const string UpdateProject = "ProjectsController.Update";
    private const string ApproveArtifact = "ArtifactsController.UpdateStatus";
    private const string RecordWork = "ArtifactsController.RecordWork";

    private static ReasonGuide ProjectField(string what) => new(CheckKinds.Action, "Not recorded",
        $"Open Requirements Gathering › Project context, record the {what} and save.",
        Writers, new(CheckAreas.Context), UpdateProject);

    private static ReasonGuide Approve(string plural, CheckDestination where) => new(CheckKinds.Approval, "Needs approval",
        $"Open each listed item, review it and approve it (or reject it with a reason). Approval of {plural} is a separate step from creating them.",
        Approvers, where, ApproveArtifact);

    private static ReasonGuide Generate(string title, string instruction, CheckDestination where, string endpoint, string[]? roles = null) =>
        new(CheckKinds.Action, title, instruction, roles ?? Writers, where, endpoint);

    private static readonly ReasonGuide GenerateFrs = Generate("No functional requirements yet",
        "Open Analysis, select a requirement source and generate its functional requirements.",
        new(CheckAreas.Analysis, "sec-ai-analysis"), "RequirementSourcesController.GenerateRequirements");

    private static readonly ReasonGuide ApproveFrsFirst = new(CheckKinds.Approval, "No approved functional requirements",
        "Functional requirements have to be approved first. Open Analysis › Functional Requirements and approve them.",
        Approvers, new(CheckAreas.Analysis, "sec-functional"), ApproveArtifact);

    private static readonly ReasonGuide GenerateTasks = Generate("No implementation task",
        "Open Development, select the requirement's source and generate implementation tasks.",
        new(CheckAreas.Development, "sec-tasks"), "DesignController.GenerateTasks");

    private static readonly ReasonGuide GenerateTests = Generate("No test case",
        "Open Testing and generate test cases for each listed functional requirement.",
        new(CheckAreas.Testing, "sec-tests"), "ArtifactsController.GenerateTestCases", TestWriters);

    private static readonly ReasonGuide CompleteTasks = new(CheckKinds.Action, "Not completed",
        "Open each listed task in Development and, once the work is done, use Work record › Mark complete.",
        [Roles.Developer], new(CheckAreas.Development, "sec-tasks"), RecordWork, AdminRecordsDontCount: true);

    // Test cases follow ArtifactStatusTransitions: a Draft is submitted for review before anyone can
    // decide on it; Under Review and AI Generated are decided directly. Text only - the rules are unchanged.
    private const string TestCaseApprovalSteps =
        "Open each listed test case in Testing. Draft (or Rejected): use Submit for review first, which moves it to Under Review; a Draft cannot be approved directly. " +
        "Under Review: a Business Analyst, Reviewer or Administrator approves it (or rejects it with a reason). " +
        "AI Generated: a Business Analyst, Reviewer or Administrator can approve it directly, without Submit for review. " +
        "QA cannot approve test cases; once a test case is approved, QA records Passed, Failed or Blocked in its Test execution section.";

    private static ReasonGuide ApproveTests(string title, string lead) => new(CheckKinds.Approval, title,
        $"{lead} {TestCaseApprovalSteps}", Approvers, new(CheckAreas.Testing, "sec-tests"), ApproveArtifact);

    private static readonly ReasonGuide ResolveConflicts = new(CheckKinds.Approval, "Unresolved pairs",
        "Open Validation › Conflicts. For each pair, keep one side and reject the other with a reason. Conflicts are never deleted; rejecting one side resolves the pair.",
        Approvers, new(CheckAreas.Validation, "sec-consistency"), ApproveArtifact);

    private static CheckGuide Check(string kind, string howTo, params (string Reason, ReasonGuide Guide)[] reasons) =>
        new(kind, howTo, reasons.ToDictionary(r => r.Reason, r => r.Guide), []);

    private static CheckGuide Automatic(string howTo, string[] dependsOn, params (string Reason, ReasonGuide Guide)[] reasons) =>
        new(CheckKinds.Automatic, howTo, reasons.ToDictionary(r => r.Reason, r => r.Guide), dependsOn);

    public static readonly IReadOnlyDictionary<string, CheckGuide> Checks = new Dictionary<string, CheckGuide>
    {
        // ---- Draft ------------------------------------------------------------------------------
        [ExitCheckKeys.BusinessProblem] = Check(CheckKinds.Action, "Record the business problem this project solves.",
            (BlockerReasons.Missing, ProjectField("business problem"))),
        [ExitCheckKeys.Objectives] = Check(CheckKinds.Action, "Record the project's business objectives.",
            (BlockerReasons.Missing, ProjectField("business objectives"))),
        [ExitCheckKeys.Scope] = Check(CheckKinds.Action, "Record what is in and out of scope.",
            (BlockerReasons.Missing, ProjectField("scope"))),
        [ExitCheckKeys.BusinessAnalystMember] = Check(CheckKinds.Action, "Add a Business Analyst to the project team.",
            (BlockerReasons.Missing, new(CheckKinds.Action, "No Business Analyst on the team",
                "Open Requirements Gathering › Members and add a user who holds the Business Analyst role.",
                Writers, new(CheckAreas.Members), "ProjectsController.AddMember"))),

        // ---- Requirements Gathering -------------------------------------------------------------
        [ExitCheckKeys.RawRequirements] = Check(CheckKinds.Action, "Capture the raw requirements the analysis works from.",
            (BlockerReasons.Missing, new(CheckKinds.Action, "Nothing captured yet",
                "Open Requirements Gathering › Requirement sources and paste the requirements text or upload a document.",
                Writers, new(CheckAreas.Sources), "RequirementSourcesController.Create"))),
        [ExitCheckKeys.Stakeholders] = Check(CheckKinds.Action, "Record at least one stakeholder.",
            (BlockerReasons.Missing, new(CheckKinds.Action, "No stakeholders yet",
                "Open Requirements Gathering › Stakeholders and add the people affected by, or deciding on, this project.",
                Writers, new(CheckAreas.Stakeholders), "ProjectsController.AddStakeholder"))),

        // ---- Analysis ---------------------------------------------------------------------------
        [ExitCheckKeys.AnalysisRun] = Check(CheckKinds.Action, "Run the AI requirement analysis on a requirement source.",
            (BlockerReasons.NotRun, new(CheckKinds.Action, "Not run yet",
                "Open Analysis, select a requirement source and run the AI requirement analysis.",
                Writers, new(CheckAreas.Analysis, "sec-ai-analysis"), "RequirementSourcesController.Analyze"))),
        [ExitCheckKeys.QuestionsAnswered] = Check(CheckKinds.Action, "Answer every open clarification question.",
            (BlockerReasons.OpenQuestions, new(CheckKinds.Action, "Open questions",
                "Open Validation › Clarification questions and answer (or dismiss) each open question.",
                Writers, new(CheckAreas.Validation, "sec-clarifications"), "ArtifactsController.AnswerClarification"))),
        [ExitCheckKeys.FrsApproved] = Check(CheckKinds.Approval, "Approve every functional requirement.",
            (BlockerReasons.NoneYet, GenerateFrs),
            (BlockerReasons.NeedsApproval, Approve("functional requirements", new(CheckAreas.Analysis, "sec-functional")))),
        [ExitCheckKeys.NfrsApproved] = Check(CheckKinds.Approval, "Approve every non-functional requirement.",
            (BlockerReasons.NeedsApproval, Approve("non-functional requirements", new(CheckAreas.Analysis, "sec-nonfunctional")))),
        [ExitCheckKeys.RulesApproved] = Check(CheckKinds.Approval, "Approve every business rule.",
            (BlockerReasons.NeedsApproval, Approve("business rules", new(CheckAreas.Analysis, "sec-rules")))),
        [ExitCheckKeys.StoriesApproved] = Check(CheckKinds.Approval, "Approve every user story.",
            (BlockerReasons.NoneYet, Generate("No user stories yet",
                "Open Analysis, select a requirement source and generate user stories.",
                new(CheckAreas.Analysis, "sec-stories"), "RequirementSourcesController.GenerateUserStories")),
            (BlockerReasons.NeedsApproval, Approve("user stories", new(CheckAreas.Analysis, "sec-stories")))),
        [ExitCheckKeys.CriteriaApproved] = Check(CheckKinds.Approval, "Approve every acceptance criterion.",
            (BlockerReasons.NoneYet, Generate("No acceptance criteria yet",
                "Open Analysis › User stories and generate acceptance criteria for each story.",
                new(CheckAreas.Analysis, "sec-stories"), "ArtifactsController.GenerateAcceptanceCriteria")),
            (BlockerReasons.NeedsApproval, Approve("acceptance criteria", new(CheckAreas.Analysis, "sec-criteria")))),
        [ExitCheckKeys.QualityRun] = Check(CheckKinds.Action, "Run the requirement quality analysis.",
            (BlockerReasons.NotRun, new(CheckKinds.Action, "Not run yet",
                "Open Validation, select a requirement source and run the quality analysis.",
                QualityRunners, new(CheckAreas.Validation, "sec-quality"), "RequirementSourcesController.AnalyzeQuality"))),
        [ExitCheckKeys.ConflictRun] = Check(CheckKinds.Action, "Run conflict detection across the project's requirements.",
            (BlockerReasons.NotRun, new(CheckKinds.Action, "Not run yet",
                "Open Validation › Conflicts and run conflict detection.",
                QualityRunners, new(CheckAreas.Validation, "sec-consistency"), "ProjectsController.DetectConflicts"))),
        [ExitCheckKeys.ConflictsResolved] = Check(CheckKinds.Approval, "Decide every conflict or duplicate pair.",
            (BlockerReasons.Unresolved, ResolveConflicts)),
        [ExitCheckKeys.StoryChain] = Check(CheckKinds.Action, "Give every functional requirement a user story with acceptance criteria.",
            (BlockerReasons.NoneYet, GenerateFrs),
            (BlockerReasons.NoStory, Generate("No user story",
                "Open Analysis, select the requirement's source and generate user stories.",
                new(CheckAreas.Analysis, "sec-stories"), "RequirementSourcesController.GenerateUserStories")),
            (BlockerReasons.NoCriteria, Generate("No acceptance criteria",
                "Open the requirement's user story in Analysis and generate acceptance criteria for it.",
                new(CheckAreas.Analysis, "sec-stories"), "ArtifactsController.GenerateAcceptanceCriteria"))),

        // ---- Design -----------------------------------------------------------------------------
        [ExitCheckKeys.DesignApproved] = Check(CheckKinds.Approval, "Approve the solution design.",
            (BlockerReasons.NoneYet, Generate("No solution design yet",
                "Open Design, select a requirement source and generate the solution design.",
                new(CheckAreas.Design, "sec-architecture"), "DesignController.GenerateDesign")),
            (BlockerReasons.NeedsApproval, Approve("solution designs", new(CheckAreas.Design, "sec-architecture")))),
        [ExitCheckKeys.ApiApproved] = Check(CheckKinds.Approval, "Approve the API design.",
            (BlockerReasons.NoneYet, Generate("No API specifications yet",
                "Open Design, select a requirement source and generate the API specifications.",
                new(CheckAreas.Design, "sec-api"), "DesignController.GenerateApiSpecs")),
            (BlockerReasons.NeedsApproval, Approve("API specifications", new(CheckAreas.Design, "sec-api")))),
        [ExitCheckKeys.DatabaseApproved] = Check(CheckKinds.Approval, "Approve the database design.",
            (BlockerReasons.NoneYet, Generate("No data entities yet",
                "Open Design, select a requirement source and generate the data entities.",
                new(CheckAreas.Design, "sec-data"), "DesignController.GenerateDataEntities")),
            (BlockerReasons.NeedsApproval, Approve("data entities", new(CheckAreas.Design, "sec-data")))),
        [ExitCheckKeys.DeveloperReview] = Check(CheckKinds.Action, "A Developer reviews the current version of every design artifact.",
            (BlockerReasons.NoneYet, Generate("No design artifacts yet",
                "Open Design, select a requirement source and generate the design first.",
                new(CheckAreas.Design, "sec-architecture"), "DesignController.GenerateDesign")),
            (BlockerReasons.NeedsDeveloperReview, new(CheckKinds.Action, "Needs a Developer review",
                "Open each listed design artifact and record a technical review in its Design review section. An edited artifact has a new version, which needs a new review.",
                [Roles.Developer], new(CheckAreas.Design), "ArtifactsController.AddCommentReview", AdminRecordsDontCount: true))),
        [ExitCheckKeys.DesignNotices] = Check(CheckKinds.Action, "Acknowledge every open impact notice on the design.",
            (BlockerReasons.OpenNotice, new(CheckKinds.Action, "Open impact notices",
                "Open each listed artifact, check the upstream change its notice describes, update the artifact if needed and acknowledge the notice.",
                NoticeAcknowledgers, new(CheckAreas.Design), "TraceabilityController.Acknowledge"))),
        [ExitCheckKeys.Baseline] = Check(CheckKinds.Approval, "Keep every requirement, rule, story and acceptance criterion approved.",
            (BlockerReasons.NeedsApproval, Approve("requirements", new(CheckAreas.Analysis))),
            (BlockerReasons.Reopened, new(CheckKinds.Approval, "Reopened requirements",
                "Open each listed requirement in Analysis, review what changed and approve it again.",
                Approvers, new(CheckAreas.Analysis), ApproveArtifact))),

        // ---- Approved ---------------------------------------------------------------------------
        [ExitCheckKeys.TasksApproved] = Check(CheckKinds.Approval, "Approve every implementation task.",
            (BlockerReasons.NoneYet, Generate("No implementation tasks yet",
                "Open Development, select a requirement source and generate implementation tasks.",
                new(CheckAreas.Development, "sec-tasks"), "DesignController.GenerateTasks")),
            (BlockerReasons.NeedsApproval, Approve("implementation tasks", new(CheckAreas.Development, "sec-tasks")))),
        [ExitCheckKeys.EveryFrHasTask] = Check(CheckKinds.Action, "Link every approved functional requirement to at least one implementation task.",
            (BlockerReasons.NoApprovedFrs, ApproveFrsFirst),
            (BlockerReasons.NoTask, GenerateTasks)),

        // ---- Implementation ---------------------------------------------------------------------
        [ExitCheckKeys.TasksCompleted] = Check(CheckKinds.Action, "A Developer marks every implementation task complete.",
            (BlockerReasons.NoneYet, Generate("No implementation tasks yet",
                "Open Development, select a requirement source and generate implementation tasks.",
                new(CheckAreas.Development, "sec-tasks"), "DesignController.GenerateTasks")),
            (BlockerReasons.NeedsApproval, new(CheckKinds.Approval, "Needs approval first",
                "Work can only be recorded on an approved task. Open each listed task in Development and approve it; a Developer can then complete it.",
                Approvers, new(CheckAreas.Development, "sec-tasks"), ApproveArtifact)),
            (BlockerReasons.NotCompleted, CompleteTasks),
            (BlockerReasons.TaskReopened, CompleteTasks with
            {
                Title = "Reopened",
                Instruction = "These tasks were reopened. Finish the remaining work, then use Work record › Mark complete again."
            })),
        [ExitCheckKeys.FrsImplemented] = Automatic(
            "Passes automatically once every approved functional requirement has all its linked tasks marked complete.",
            [ExitCheckKeys.TasksCompleted, ExitCheckKeys.EveryFrHasTask],
            (BlockerReasons.NoApprovedFrs, ApproveFrsFirst),
            (BlockerReasons.NoTask, GenerateTasks),
            (BlockerReasons.WaitingOnTasks, CompleteTasks with
            {
                Title = "Waiting on tasks",
                Instruction = "Complete the tasks listed for each requirement (Development › Work record › Mark complete)."
            })),

        // ---- Testing ----------------------------------------------------------------------------
        [ExitCheckKeys.TestsApproved] = Check(CheckKinds.Approval, "Get every test case approved (submit Drafts for review first).",
            (BlockerReasons.NoneYet, GenerateTests with { Title = "No test cases yet" }),
            (BlockerReasons.NeedsApproval, ApproveTests("Needs approval", "Approval of test cases is a separate step from creating them."))),
        [ExitCheckKeys.EveryFrHasApprovedTest] = Check(CheckKinds.Action, "Give every approved functional requirement at least one approved test case.",
            (BlockerReasons.NoApprovedFrs, ApproveFrsFirst),
            (BlockerReasons.NoTest, GenerateTests),
            (BlockerReasons.TestNeedsApproval, ApproveTests("Test case not approved", "Each requirement needs an approved test case."))),
        [ExitCheckKeys.TestsPassed] = Check(CheckKinds.Action, "QA records a passing result for every test case.",
            (BlockerReasons.NoneYet, GenerateTests with { Title = "No test cases yet" }),
            (BlockerReasons.NeedsApproval, ApproveTests("Needs approval first", "A result can only be recorded on an approved test case.")),
            (BlockerReasons.NoResult, new(CheckKinds.Action, "No result recorded",
                "Run each listed test case and record the outcome in its Work record (Passed, Failed or Blocked).",
                [Roles.QA], new(CheckAreas.Testing, "sec-tests"), RecordWork, AdminRecordsDontCount: true)),
            (BlockerReasons.Failed, new(CheckKinds.Action, "Failed",
                "Get the defect fixed (a Developer reopens and completes the linked task), then rerun the test and record Passed.",
                [Roles.QA], new(CheckAreas.Testing, "sec-tests"), RecordWork, AdminRecordsDontCount: true)),
            (BlockerReasons.Blocked, new(CheckKinds.Action, "Blocked",
                "Clear whatever blocks the test, rerun it and record Passed.",
                [Roles.QA], new(CheckAreas.Testing, "sec-tests"), RecordWork, AdminRecordsDontCount: true))),
        [ExitCheckKeys.FrsVerified] = Automatic(
            "Passes automatically once every approved functional requirement is implemented and all its linked test cases have passed.",
            [ExitCheckKeys.TasksCompleted, ExitCheckKeys.EveryFrHasApprovedTest, ExitCheckKeys.TestsPassed],
            (BlockerReasons.NoApprovedFrs, ApproveFrsFirst),
            (BlockerReasons.NoTask, GenerateTasks),
            (BlockerReasons.NotImplemented, CompleteTasks with
            {
                Title = "Not implemented yet",
                Instruction = "Complete the tasks listed for each requirement (Development › Work record › Mark complete)."
            }),
            (BlockerReasons.NoTest, GenerateTests),
            (BlockerReasons.TestsNotPassed, new(CheckKinds.Action, "Tests not passed",
                "Run the test cases listed for each requirement and record Passed in their Work record.",
                [Roles.QA], new(CheckAreas.Testing, "sec-tests"), RecordWork, AdminRecordsDontCount: true))),
        [ExitCheckKeys.FullyTraced] = Check(CheckKinds.Action,
            "Trace every approved functional requirement to a story, acceptance criteria, design, task and test case.",
            (BlockerReasons.NoApprovedFrs, ApproveFrsFirst),
            (BlockerReasons.TraceGaps, new(CheckKinds.Action, "Trace gaps",
                "Open Traceability to see each requirement's missing links, then create them on their phase pages: stories and acceptance criteria in Analysis, design in Design, tasks in Development, test cases in Testing (QA can also generate test cases).",
                Writers, new(CheckAreas.Traceability), null))),
        [ExitCheckKeys.TestingNotices] = Check(CheckKinds.Action, "Acknowledge every open impact notice.",
            (BlockerReasons.OpenNotice, new(CheckKinds.Action, "Open impact notices",
                "Open each listed artifact, check the upstream change its notice describes, update the artifact if needed and acknowledge the notice.",
                NoticeAcknowledgers, new(CheckAreas.Testing), "TraceabilityController.Acknowledge"))),
        [ExitCheckKeys.QaSignOff] = Check(CheckKinds.SignOff, "QA signs off Testing when submitting it for Reviewer approval.",
            (BlockerReasons.SignOff, new(CheckKinds.SignOff, "QA sign-off",
                "QA submits Testing for Reviewer approval from the lifecycle panel and writes a sign-off note.",
                [Roles.QA], new(CheckAreas.Lifecycle), null))),
    };

    public static bool TryGet(string key, out CheckGuide guide) => Checks.TryGetValue(key, out guide!);
}
