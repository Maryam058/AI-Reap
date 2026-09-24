using AiReap.Application.Ai;

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
            _ when systemPrompt.Contains("missingInformation") => AnalysisResponse,
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
            _ when systemPrompt.Contains("testCases") => TestCasesResponse,
            _ when systemPrompt.Contains("citedChunkRefs") => CopilotResponse,
            _ => "{}"
        };

        // Canned responses link to "FR-001" / "BO-001". Like a real model, only reference artifacts
        // that were actually given in the prompt (reference validation rejects anything else): use
        // the first such code present, or no reference at all when there is none.
        response = ReferenceFirstCodeInPrompt(response, userPrompt, "FR");
        response = ReferenceFirstCodeInPrompt(response, userPrompt, "BO");

        return Task.FromResult(response);
    }

    private static string ReferenceFirstCodeInPrompt(string response, string userPrompt, string prefix)
    {
        var placeholder = $"\"{prefix}-001\"";
        if (!response.Contains(placeholder, StringComparison.Ordinal))
        {
            return response;
        }

        var firstCode = System.Text.RegularExpressions.Regex.Match(userPrompt, $@"\b{prefix}-\d{{3,}}\b");
        return firstCode.Success
            ? response.Replace(placeholder, $"\"{firstCode.Value}\"", StringComparison.Ordinal)
            : response.Replace($"[{placeholder}]", "[]", StringComparison.Ordinal);
    }

    private const string ReviewResponse = """
        {
          "readinessSummary": "Stub assessment - configure Ai:Gemini:ApiKey for a real model. See the deterministic findings for the actual gaps.",
          "recommendation": "not_ready"
        }
        """;

    private const string AnalysisResponse = """
        {
          "actors": ["Employee", "Manager"],
          "capabilities": ["Submit request", "Review request", "Approve or reject request"],
          "dataElements": ["Request type", "Start date", "End date", "Status"],
          "notes": ["This is a stub response — configure Ai:Gemini:ApiKey for a real model."],
          "missingInformation": [
            {"topic": "Approval hierarchy", "question": "Can approval be delegated, and is multi-level approval required?", "reason": "Not stated in the raw requirement."},
            {"topic": "Notifications", "question": "Should the submitter be notified when the request is approved or rejected?", "reason": "Not stated in the raw requirement."}
          ]
        }
        """;

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
          "architectureOverview": "Stub response — configure Ai:Gemini:ApiKey for a grounded design proposal.",
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

    private const string CopilotResponse = """
        {
          "answer": "Stub response — configure Ai:Gemini:ApiKey for a grounded answer. Check the structured facts above for the actual current counts.",
          "citedChunkRefs": [],
          "relatedArtifactCodes": []
        }
        """;
}
