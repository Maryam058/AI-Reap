using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;

namespace AiReap.Infrastructure.Ai;

// Registered when no real provider API key is configured (see ADR-001 §1), so the app
// builds, runs, and demoes the full requirement pipeline without requiring a live API key.
// Every pipeline stage (RequirementAnalysisService, RequirementGenerationService,
// UserStoryService) demands strict JSON (REAP-005), so the stub pattern-matches on distinct
// markers in the system prompt to return a plausible canned response for that stage rather
// than one generic shape.
public class StubAiChatClient : IAiChatClient
{
    public string ModelName => "stub-canned-model";

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var response = systemPrompt switch
        {
            _ when systemPrompt.Contains("readinessSummary") => ReviewResponse,
            _ when systemPrompt.Contains("missingInformation") => AnalysisResponse(userPrompt),
            _ when systemPrompt.Contains("functionalRequirements") => RequirementsResponse,
            _ when systemPrompt.Contains("userStories") => UserStoriesResponse,
            _ when systemPrompt.Contains("acceptanceCriteria") => AcceptanceCriteriaResponse,
            _ when systemPrompt.Contains("businessRules") => BusinessRulesResponse,
            _ when systemPrompt.Contains("relationshipType") => ConflictsResponse,
            _ when systemPrompt.Contains("artifactCode") => QualityResponse,
            _ when systemPrompt.Contains("architectureOverview") => DesignResponse,
            _ when systemPrompt.Contains("dataEntities") => DataEntitiesResponse,
            _ when systemPrompt.Contains("apiSpecifications") => ApiSpecsResponse,
            _ when systemPrompt.Contains("taskType") => TasksResponse,
            // A non-functional requirement's prompt (TestGenerationService.NfrPrompt) gets quality test
            // cases, so the stub exercises the NFR test kinds rather than the FR ones.
            _ when systemPrompt.Contains("testCases") && userPrompt.Contains("\nTarget status: ") => NfrTestCasesResponse,
            _ when systemPrompt.Contains("testCases") => TestCasesResponse,
            _ when systemPrompt.Contains("citedChunkRefs") => CopilotResponse,
            _ => "{}"
        };

        // Canned responses link to "FR-001" / "BO-001". Like a real model, only reference artifacts
        // that were actually given in the prompt (reference validation rejects anything else): use
        // the first such code present, or no reference at all when there is none. An FR code is
        // taken only from the eligible-FR list under its heading - never from an FR-like ID written
        // in the raw source text above it ("FR-012" there is a document ID, not a stored code).
        response = ReferenceFirstCode(response, "FR", FirstListedRequirementCode(userPrompt));
        response = ReferenceFirstCode(response, "BO", FirstCodeInPrompt(userPrompt, "BO"));

        return Task.FromResult(response);
    }

    private static string ReferenceFirstCode(string response, string prefix, string? code)
    {
        var placeholder = $"\"{prefix}-001\"";
        if (!response.Contains(placeholder, StringComparison.Ordinal))
        {
            return response;
        }

        return code is not null
            ? response.Replace(placeholder, $"\"{code}\"", StringComparison.Ordinal)
            : response.Replace($"[{placeholder}]", "[]", StringComparison.Ordinal);
    }

    // The first "- FR-001: Title" line directly under the last FunctionalRequirementsHeading (the
    // services append the list after the raw text); null when the prompt carries no such list.
    private static string? FirstListedRequirementCode(string userPrompt)
    {
        var heading = GenerationSupport.FunctionalRequirementsHeading;
        var at = userPrompt.LastIndexOf(heading, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var lines = userPrompt[(at + heading.Length)..].Split('\n');
        var first = lines.Length > 1 ? lines[1].TrimEnd('\r') : string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(first, @"^- (FR-\d{3,}): ");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? FirstCodeInPrompt(string userPrompt, string prefix)
    {
        var match = System.Text.RegularExpressions.Regex.Match(userPrompt, $@"\b{prefix}-\d{{3,}}\b");
        return match.Success ? match.Value : null;
    }

    private const string ReviewResponse = """
        {
          "readinessSummary": "Stub assessment - configure Ai:Anthropic:ApiKey (Claude) for a real model. See the deterministic findings for the actual gaps.",
          "recommendation": "not_ready"
        }
        """;

    // Clarification questions must quote the source they are about (ClarificationCandidateValidator), so
    // the stub grounds its two questions in the text it was sent: the first two statements of at least
    // four words (or the whole text when it has none), each quoted verbatim and named in its question.
    private static string AnalysisResponse(string userPrompt)
    {
        var text = userPrompt.StartsWith("[AI-REAP note:", StringComparison.Ordinal) && userPrompt.IndexOf("]\n\n", StringComparison.Ordinal) is var end and > 0
            ? userPrompt[(end + 3)..]
            : userPrompt;
        var statements = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+(\.\d+)*\.?\s+\S+$"))
            .SelectMany(l => System.Text.RegularExpressions.Regex.Split(l, @"(?<=[.;!?])\s+"))
            .Select(s => s.Trim())
            .Where(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4)
            .Take(2)
            .ToList();
        if (statements.Count == 0 && text.Trim().Length > 0) statements.Add(text.Trim());
        if (statements.Count == 1) statements.Add(statements[0]);

        static string Short(string statement) =>
            string.Join(' ', statement.TrimEnd('.', ';', '!', '?', ':').Replace("?", string.Empty).Replace("\"", "'")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6));

        var questions = statements.Count == 0
            ? new System.Text.Json.Nodes.JsonArray()
            : new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = "ambiguous",
                    ["topic"] = $"Who may: {Short(statements[0])}",
                    ["question"] = $"Which user roles are allowed to perform \"{Short(statements[0])}\"?",
                    ["reason"] = "The statement does not say which roles it applies to.",
                    ["impact"] = "Access rules and the acceptance tests depend on it.",
                    ["evidence"] = new System.Text.Json.Nodes.JsonArray(statements[0])
                },
                new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = "missing",
                    ["topic"] = $"Acceptance: {Short(statements[1])}",
                    ["question"] = $"What measurable acceptance condition confirms \"{Short(statements[1])}\" is met?",
                    ["reason"] = "No measurable outcome is stated for this statement.",
                    ["impact"] = "Without it the requirement cannot be verified.",
                    ["evidence"] = new System.Text.Json.Nodes.JsonArray(statements[1])
                });

        return new System.Text.Json.Nodes.JsonObject
        {
            ["actors"] = new System.Text.Json.Nodes.JsonArray("Employee", "Manager"),
            ["capabilities"] = new System.Text.Json.Nodes.JsonArray("Submit request", "Review request", "Approve or reject request"),
            ["dataElements"] = new System.Text.Json.Nodes.JsonArray("Request type", "Start date", "End date", "Status"),
            ["notes"] = new System.Text.Json.Nodes.JsonArray("This is a stub response — configure Ai:Anthropic:ApiKey (Claude) for a real model."),
            ["missingInformation"] = questions
        }.ToJsonString();
    }

    private const string RequirementsResponse = """
        {
          "functionalRequirements": [
            {"title": "Submit request", "actor": "Employee", "priority": "High",
             "preconditions": "Employee is authenticated.", "inputs": "Request type, details",
             "processing": "Validate input and persist the request.", "expectedResult": "Request stored with status Pending.",
             "dependencies": [], "businessObjectiveCodes": ["BO-001"]}
          ],
          "nonFunctionalRequirements": [
            {"title": "Response time", "category": "Performance",
             "description": "The system should respond to user actions promptly.",
             "targetValue": "stub value — configure a real model for a grounded proposal"}
          ]
        }
        """;

    private const string UserStoriesResponse = """
        {
          "userStories": [
            {"title": "Submit a request", "persona": "Employee",
             "valueStatement": "As an employee, I want to submit a request so that it can be reviewed.",
             "priority": "High", "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private const string AcceptanceCriteriaResponse = """
        {
          "acceptanceCriteria": [
            {"title": "Valid submission", "given": "the employee is authenticated",
             "when": "they submit a valid request", "then": "the request is stored with status Pending", "kind": "positive"},
            {"title": "Missing required field", "given": "the employee is authenticated",
             "when": "they submit a request missing a required field", "then": "the system rejects it with a validation error", "kind": "negative"}
          ]
        }
        """;

    private const string BusinessRulesResponse = """
        {
          "businessRules": [
            {"title": "Approval threshold", "statement": "Requests exceeding a defined threshold require second-level approval (stub — configure a real model for a grounded rule).",
             "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private const string ConflictsResponse = """
        { "findings": [] }
        """;

    private const string QualityResponse = """
        { "findings": [] }
        """;

    private const string DesignResponse = """
        {
          "title": "Solution Design",
          "architectureOverview": "Stub response — configure Ai:Anthropic:ApiKey (Claude) for a grounded design proposal.",
          "modules": ["RequestService"], "integrationPoints": null, "authentication": "JWT bearer",
          "backgroundProcessing": null, "caching": null, "logging": "Structured logging via the platform's standard middleware.",
          "deploymentConsiderations": null
        }
        """;

    private const string DataEntitiesResponse = """
        {
          "dataEntities": [
            {"title": "Request",
             "fields": [{"name": "Id", "dataType": "Guid", "required": true}, {"name": "Status", "dataType": "string", "required": true}],
             "keys": ["Id"], "relationships": null, "indexes": ["Status"], "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private const string ApiSpecsResponse = """
        {
          "apiSpecifications": [
            {"title": "Submit request", "purpose": "Create a new request", "method": "POST", "route": "/api/requests",
             "request": "{ type, details }", "response": "201 Created with the new request", "validation": "type and details are required",
             "authorization": "Authenticated user", "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private const string TasksResponse = """
        {
          "tasks": [
            {"title": "Create Request database entity", "description": "Stub response — configure a real model for a grounded task breakdown.",
             "taskType": "Database", "priority": "High", "dependencies": [], "suggestedRole": "Developer", "estimate": null,
             "relatedRequirementCodes": ["FR-001"]}
          ]
        }
        """;

    private const string TestCasesResponse = """
        {
          "testCases": [
            {"title": "Submit a valid request", "preconditions": "User is authenticated", "steps": ["Open the request form", "Fill in valid details", "Submit"],
             "expectedResult": "Request is created with status Pending", "testKind": "positive"},
            {"title": "Submit with missing required field", "preconditions": "User is authenticated", "steps": ["Open the request form", "Leave a required field blank", "Submit"],
             "expectedResult": "System rejects the request with a validation error", "testKind": "validation"}
          ]
        }
        """;

    // No number of its own: the pass criterion is "the agreed target", as the NFR prompt requires
    // when a target is not confirmed.
    private const string NfrTestCasesResponse = """
        {
          "testCases": [
            {"title": "Response time under expected peak load", "preconditions": "Test environment loaded with production-like data", "steps": ["Generate the expected peak number of concurrent users", "Measure the response time of each request for the duration of the test"],
             "expectedResult": "Response times meet the agreed target (to be confirmed if the target is still an assumption)", "testKind": "performance"},
            {"title": "Access is refused without authentication", "preconditions": "No signed-in session", "steps": ["Request the protected page or API directly", "Repeat with an expired session"],
             "expectedResult": "Access is refused and no requirement data is returned", "testKind": "security"}
          ]
        }
        """;

    private const string CopilotResponse = """
        {
          "answer": "Stub response — configure Ai:Anthropic:ApiKey (Claude) for a grounded answer. Check the structured facts above for the actual current counts.",
          "citedChunkRefs": [],
          "relatedArtifactCodes": []
        }
        """;
}
