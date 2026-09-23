using System.Reflection;
using AiReap.Api.Authorization;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace AiReap.Api.Controllers;

// §4/§38 - "Roles & permissions" (web/src/pages/RolesPage.tsx) used to hand-mirror the API's
// [Authorize(Roles = ...)] rules as static TS constants, which could silently drift from the
// real rules (as it already had: the §38 project-membership endpoints were never added to it).
// This endpoint reflects over the actually-loaded controller actions instead, so the roles shown
// are always the roles the API itself enforces. Only actions tagged [Capability] are reported -
// that tag also carries the human-readable grouping/label the raw route can't express - and where
// several actions share one capability label, the tagged action is the one treated as authoritative.
[ApiController]
[Authorize]
public class RolesController : ControllerBase
{
    private readonly IActionDescriptorCollectionProvider _actionDescriptors;

    public RolesController(IActionDescriptorCollectionProvider actionDescriptors)
    {
        _actionDescriptors = actionDescriptors;
    }

    [HttpGet("api/roles/matrix")]
    public ActionResult<IReadOnlyList<CapabilityGroupResponse>> GetMatrix()
    {
        var rows = _actionDescriptors.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(a => (Action: a, Capability: a.MethodInfo.GetCustomAttribute<CapabilityAttribute>()))
            .Where(x => x.Capability is not null)
            .OrderBy(x => x.Capability!.Order)
            .Select(x => (x.Capability!.Group, Item: new CapabilityResponse(x.Capability.Label, EffectiveRoles(x.Action))))
            .ToList();

        var groups = rows
            .GroupBy(x => x.Group)
            .Select(g => new CapabilityGroupResponse(g.Key, g.Select(x => x.Item).ToList()))
            .ToList();

        return Ok(groups);
    }

    // The roles actually enforced for an action: its own [Authorize(Roles=...)] if it has one,
    // else its controller's, else - a bare [Authorize] with no Roles - every defined role, since
    // that's what "any authenticated user" means in an app with a fixed, known role set.
    private static IReadOnlyList<string> EffectiveRoles(ControllerActionDescriptor action)
    {
        var roles = action.MethodInfo.GetCustomAttribute<AuthorizeAttribute>()?.Roles
            ?? action.ControllerTypeInfo.GetCustomAttribute<AuthorizeAttribute>()?.Roles;

        return string.IsNullOrWhiteSpace(roles)
            ? Roles.All
            : roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
