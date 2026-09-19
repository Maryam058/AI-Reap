namespace AiReap.Domain.Common;

// §4 — the five fixed roles. Backed by ASP.NET Identity roles seeded at startup.
public static class Roles
{
    public const string Administrator = "Administrator";
    public const string BusinessAnalyst = "BusinessAnalyst";
    public const string Developer = "Developer";
    public const string QA = "QA";
    public const string Reviewer = "Reviewer";

    public static readonly string[] All =
    {
        Administrator, BusinessAnalyst, Developer, QA, Reviewer
    };
}
