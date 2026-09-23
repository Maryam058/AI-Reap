namespace AiReap.Api.Authorization;

// Marks the one controller action that best represents a user-facing capability, so
// GET /api/roles/matrix (RolesController) can report the *real* [Authorize(Roles = ...)] for
// that capability instead of a hand-maintained copy on the frontend (see web/src/pages/RolesPage.tsx).
// `order` controls both the row's position and, since rows are grouped by `group` after sorting
// by `order`, the group's position - keep it in the sequence the roles page should read top to bottom.
[AttributeUsage(AttributeTargets.Method)]
public sealed class CapabilityAttribute : Attribute
{
    public string Group { get; }
    public string Label { get; }
    public int Order { get; }

    public CapabilityAttribute(string group, string label, int order)
    {
        Group = group;
        Label = label;
        Order = order;
    }
}
