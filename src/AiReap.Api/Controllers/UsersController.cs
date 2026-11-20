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
    public async Task<ActionResult<UserResponse>> ChangeRole(string id, UpdateUserRoleRequest request, [FromServices] AiReap.Infrastructure.Persistence.AiReapDbContext db)
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
            // One transaction: the user never ends up with no role, or with both, if a step fails.
            var failure = await InTransactionAsync(db,
                () => _userManager.RemoveFromRolesAsync(user, current),
                () => _userManager.AddToRoleAsync(user, request.Role),
                () => _userManager.UpdateSecurityStampAsync(user)); // sign the user out of their old-role tokens
            if (failure is not null)
            {
                return failure;
            }

            _logger.LogInformation("Administrator {Admin} changed role of {UserId} from [{Old}] to {New}.",
                CallerId, id, string.Join(",", current), request.Role);
        }

        return Ok(await ToResponseAsync(user));
    }

    // Runs the Identity steps in one database transaction, stopping at the first that fails; on any
    // failure nothing is kept and the caller gets a problem response naming it.
    private async Task<ActionResult?> InTransactionAsync(Infrastructure.Persistence.AiReapDbContext db, params Func<Task<IdentityResult>>[] steps)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var step in steps)
        {
            var result = await step();
            if (!result.Succeeded)
            {
                await transaction.RollbackAsync();
                _logger.LogWarning("User update by {Admin} rolled back: {Errors}", CallerId, string.Join("; ", result.Errors.Select(e => e.Code)));
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The user was not changed.",
                    detail: string.Join(" ", result.Errors.Select(e => e.Description)));
            }
        }

        await transaction.CommitAsync();
        return null;
    }

    [HttpPut("{id}/status")]
    public async Task<ActionResult<UserResponse>> SetStatus(string id, UpdateUserStatusRequest request, [FromServices] AiReap.Infrastructure.Persistence.AiReapDbContext db)
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

        // Deactivation = Identity lockout with no end date (no separate flag/column needed). One
        // transaction, so a user is never left deactivated with old tokens still valid, or half-changed.
        var failure = await InTransactionAsync(db,
            () => _userManager.SetLockoutEnabledAsync(user, true),
            () => _userManager.SetLockoutEndDateAsync(user, request.IsActive ? null : DateTimeOffset.MaxValue),
            () => _userManager.UpdateSecurityStampAsync(user));
        if (failure is not null)
        {
            return failure;
        }

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
