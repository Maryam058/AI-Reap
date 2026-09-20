using AiReap.Api.Auth;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtTokenService _tokenService;

    public AuthController(UserManager<ApplicationUser> userManager, JwtTokenService tokenService)
    {
        _userManager = userManager;
        _tokenService = tokenService;
    }

    // Public sign-up. The caller cannot choose a role: the account is created with none, so it
    // can sign in but every role-gated and [Authorize] endpoint refuses it (see the default
    // authorization policy in Program.cs) until an Administrator assigns a role.
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
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

        var token = _tokenService.CreateToken(user, Array.Empty<string>());
        return Ok(new AuthResponse(token, user.Id, user.Email!, user.DisplayName, Array.Empty<string>()));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            return Unauthorized("Invalid email or password.");
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "This account has been deactivated. Contact an administrator.");
        }

        var roles = await _userManager.GetRolesAsync(user);
        var token = _tokenService.CreateToken(user, roles);
        return Ok(new AuthResponse(token, user.Id, user.Email!, user.DisplayName, roles));
    }
}
