using AiReap.Api.Authorization;
using AiReap.Application.Settings;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// Administrator-only AI configuration and system settings (§4: Administrator manages AI
// configuration and system settings). Enforced here on the server; the React page merely hides
// the link from everyone else. Read-only: provider credentials and options live in server
// configuration and are never returned (see SystemSettingsResponse).
[ApiController]
[Route("api/settings")]
[Authorize(Roles = Roles.Administrator)]
public class SettingsController : ControllerBase
{
    private readonly ISystemSettingsService _settings;

    public SettingsController(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet]
    [Capability("Administer", "View AI configuration and system settings", 110)]
    public async Task<ActionResult<SystemSettingsResponse>> Get(CancellationToken cancellationToken)
    {
        return Ok(await _settings.GetAsync(cancellationToken));
    }
}
