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

    // A hash of a random password, made once with the configured hasher, for the unknown-email path.
    private static readonly ApplicationUser UnknownUser = new();
    private static readonly Lazy<string> UnknownUserHash = new(() =>
        new PasswordHasher<ApplicationUser>().HashPassword(UnknownUser, Guid.NewGuid().ToString("N")));

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
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, [FromServices] LoginThrottle throttle)
    {
        // Refused before the password is checked, so a blocked account cannot be guessed at (LoginThrottle).
        var address = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (throttle.RetryAfter(request.Email, address) is { } wait)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
            Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                $"Too many failed sign-in attempts for this account. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // An unknown email costs the same password-hash check as a known one, so the response time
            // does not tell which addresses have accounts.
            _userManager.PasswordHasher.VerifyHashedPassword(UnknownUser, UnknownUserHash.Value, request.Password ?? string.Empty);
            throttle.RecordFailure(request.Email, address);
            return Unauthorized("Invalid email or password.");
        }

        if (!await _userManager.CheckPasswordAsync(user, request.Password))
        {
            throttle.RecordFailure(request.Email, address);
            return Unauthorized("Invalid email or password.");
        }

        throttle.RecordSuccess(request.Email, address);

        if (await _userManager.IsLockedOutAsync(user))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "This account has been deactivated. Contact an administrator.");
        }

        var roles = await _userManager.GetRolesAsync(user);
        var token = _tokenService.CreateToken(user, roles);
        return Ok(new AuthResponse(token, user.Id, user.Email!, user.DisplayName, roles));
    }
}
