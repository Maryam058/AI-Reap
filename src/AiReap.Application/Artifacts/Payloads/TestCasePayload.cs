namespace AiReap.Application.Artifacts.Payloads;

// §20 — ArtifactType.TestCase.
public class TestCasePayload
{
    public string? Preconditions { get; set; }
    public List<string> Steps { get; set; } = new();
    public string ExpectedResult { get; set; } = string.Empty;
    public string TestKind { get; set; } = "positive"; // positive|negative|boundary|permission|validation
}
