using AiReap.Application.Common;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AiReap.Api.Authorization;

// Registered globally in Program.cs, so every action whose route has a literal {projectId:guid}
// segment is covered automatically - no per-controller/action attribute, and new routes are
// covered without remembering to add one. Actions where the project can only be resolved
// indirectly (an artifact, requirement source, or agent run id) can't be covered here, since the
// project isn't known until that entity is loaded from the database - those call
// IProjectAccessService.EnsureMemberAsync themselves, inside the relevant Application service,
// right after loading the entity.
public class ProjectMembershipFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.RouteData.Values.TryGetValue("projectId", out var raw) && Guid.TryParse(raw?.ToString(), out var projectId))
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<IProjectAccessService>();
            await access.EnsureMemberAsync(projectId, context.HttpContext.RequestAborted);
        }

        await next();
    }
}
