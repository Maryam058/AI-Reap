namespace AiReap.Domain.Entities;

public class ProjectStakeholder
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? RoleInProject { get; set; }
    public string? ContactInfo { get; set; }
}
