using AiReap.Api.Auth;
using AiReap.Api.Authorization;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Api.Controllers;

// Administrator-only user administration (§4: Administrator manages users). Authorization is
// enforced here on the server; the React page merely hides the link from everyone else.
//
// Uses the existing ASP.NET Identity store and JwtTokenService — no separate user table.
// Every change that should affect a signed-in user (role change, deactivation) rotates the
// user's security stamp, which invalidates their existing tokens (TokenValidation).
//
// The caller can't modify their own account here. That also guarantees at least one active
// Administrator always remains: the caller is one, and can't demote or deactivate themselves.
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Administrator)]
public class UsersController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<UsersController> _logger;

    public UsersController(UserManager<ApplicationUser> userManager, ILogger<UsersController> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet]
    [Capability("Administer", "Create users, assign roles, activate and deactivate accounts", 100)]
    public async Task<ActionResult<IReadOnlyList<UserResponse>>> List(CancellationToken cancellationToken)
    {
        var users = await _userManager.Users.OrderBy(u => u.Email).ToListAsync(cancellationToken);

        var responses = new List<UserResponse>(users.Count);
        foreach (var user in users)
        {
            responses.Add(await ToResponseAsync(user));
        }

        return Ok(responses);
    }

    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request)
    {
        if (!Roles.All.Contains(request.Role))
        {
            return BadRequest($"Role must be one of: {string.Join(", ", Roles.All)}");
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors.Select(e => e.Description));
        }

        await _userManager.AddToRoleAsync(user, request.Role);
        _logger.LogInformation("Administrator {Admin} created user {UserId} with role {Role}.", CallerId, user.Id, request.Role);

        return Created($"api/users/{user.Id}", await ToResponseAsync(user));
    }

    [HttpPut("{id}/role")]
    public async Task<ActionResult<UserResponse>> ChangeRole(string id, UpdateUserRoleRequest request)
    {
        if (!Roles.All.Contains(request.Role))
        {
            return BadRequest($"Role must be one of: {string.Join(", ", Roles.All)}");
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (id == CallerId)
        {
            return Conflict("You cannot change your own role.");
        }

        var current = await _userManager.GetRolesAsync(user);
        if (!current.SequenceEqual(new[] { request.Role }))
        {
            await _userManager.RemoveFromRolesAsync(user, current);
            await _userManager.AddToRoleAsync(user, request.Role);
            await _userManager.UpdateSecurityStampAsync(user); // sign the user out of their old-role tokens
            _logger.LogInformation("Administrator {Admin} changed role of {UserId} from [{Old}] to {New}.",
                CallerId, id, string.Join(",", current), request.Role);
        }

        return Ok(await ToResponseAsync(user));
    }

    [HttpPut("{id}/status")]
    public async Task<ActionResult<UserResponse>> SetStatus(string id, UpdateUserStatusRequest request)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (id == CallerId)
        {
            return Conflict("You cannot deactivate your own account.");
        }

        // Deactivation = Identity lockout with no end date (no separate flag/column needed).
        await _userManager.SetLockoutEnabledAsync(user, true);
        await _userManager.SetLockoutEndDateAsync(user, request.IsActive ? null : DateTimeOffset.MaxValue);
        await _userManager.UpdateSecurityStampAsync(user);
        _logger.LogInformation("Administrator {Admin} set user {UserId} active={Active}.", CallerId, id, request.IsActive);

        return Ok(await ToResponseAsync(user));
    }

    private string? CallerId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private async Task<UserResponse> ToResponseAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);
        return new UserResponse(user.Id, user.Email ?? string.Empty, user.DisplayName, roles.FirstOrDefault(),
            !await _userManager.IsLockedOutAsync(user));
    }
}
