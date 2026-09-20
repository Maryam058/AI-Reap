using System.ComponentModel.DataAnnotations;

namespace AiReap.Api.Auth;

// Plain classes, not positional records: ASP.NET Core's model validator does not reliably
// bind DataAnnotations declared on record primary-constructor parameters.
public class RegisterRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    // Deliberately no Role: public sign-up never lets the caller choose (or even suggest) one.
    // Accounts start with no role; an Administrator assigns it (see UsersController).
}

public class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public record AuthResponse(string Token, string UserId, string Email, string DisplayName, IList<string> Roles);

// Administrator-only user management (UsersController).
public class CreateUserRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [Required, MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = string.Empty;
}

public class UpdateUserRoleRequest
{
    [Required]
    public string Role { get; set; } = string.Empty;
}

public class UpdateUserStatusRequest
{
    public bool IsActive { get; set; }
}

// Role is null for an account that has signed up but not yet been assigned one.
public record UserResponse(string Id, string Email, string DisplayName, string? Role, bool IsActive);
