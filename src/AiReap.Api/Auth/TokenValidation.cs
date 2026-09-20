using System.Security.Claims;
using AiReap.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;

namespace AiReap.Api.Auth;

public static class AuthClaims
{
    // The user's Identity security stamp at the time the token was issued.
    public const string SecurityStamp = "sst";
}

// A JWT alone would keep granting its roles until it expires (ExpiryMinutes). Checking the
// user and their security stamp on every request makes role changes, deactivation and password
// changes take effect immediately: the stamp is rotated by those operations (UsersController),
// so any token issued before them stops validating.
public static class TokenValidation
{
    public static async Task ValidateUserStillCurrentAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();

        var user = userId is null ? null : await userManager.FindByIdAsync(userId);
        if (user is null
            || await userManager.IsLockedOutAsync(user)
            || principal!.FindFirstValue(AuthClaims.SecurityStamp) != user.SecurityStamp)
        {
            context.Fail("Session is no longer valid. Please sign in again.");
        }
    }
}
